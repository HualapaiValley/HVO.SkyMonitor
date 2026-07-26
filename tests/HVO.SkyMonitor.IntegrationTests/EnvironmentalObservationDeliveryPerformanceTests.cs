using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class EnvironmentalObservationDeliveryPerformanceTests
{
    private const int D1ObservationCount = 10_000;
    private const int D1BatchSize = 100;
    private const int D1WarmupBatches = 5;
    private const int D1MeasuredBatches = 30;
    private const int D2WarmupOperations = 20;
    private const int D2MeasuredOperations = 200;
    private const int D3ObservationCount = 600;
    private const string RepeatCommand = "dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --no-build --configuration Release --filter \"FullyQualifiedName~EnvironmentalObservationDeliveryPerformanceTests.Issue157_D1ToD5_RecordsReproducibleDeliveryEvidence\"";
    private static readonly DateTimeOffset Epoch = new(2026, 7, 17, 6, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly Guid PrimarySiteId = DeterministicGuid(1_000_000);
    private static readonly Guid PrimaryRegistrationId = DeterministicGuid(1_000_001);
    private static readonly Guid PrimaryAgentId = DeterministicGuid(1_000_002);
    private static readonly Guid ExpiredSiteId = DeterministicGuid(1_000_003);
    private static readonly Guid ExpiredRegistrationId = DeterministicGuid(1_000_004);
    private static readonly Guid ExpiredAgentId = DeterministicGuid(1_000_005);
    private static readonly Guid MovedSiteId = DeterministicGuid(1_000_006);
    private static readonly Guid ReprovisionedRegistrationId = DeterministicGuid(1_000_007);
    private static readonly Guid ReprovisionedAgentId = DeterministicGuid(1_000_008);
    private static readonly Guid[] IsolatedSiteIds = [PrimarySiteId, ExpiredSiteId, MovedSiteId];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Manual")]
    public async Task Issue157_D1ToD5_RecordsReproducibleDeliveryEvidence()
    {
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            AppContext.BaseDirectory,
            StringComparison.Ordinal,
            "Canonical issue #157 evidence must be collected from a Release build.");

        var repositoryRoot = FindRepositoryRoot();
        var revision = ReadRevision(repositoryRoot);
        var evidenceRevision = revision.Dirty ? "local-dirty" : revision.CandidateSha;
        var evidenceDirectory = Path.Combine(
            repositoryRoot,
            "tests",
            "HVO.SkyMonitor.IntegrationTests",
            "TestResults",
            "environmental-delivery",
            evidenceRevision);
        var workRoot = Path.Combine(Path.GetTempPath(), $"issue-157-environmental-delivery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(evidenceDirectory);
        Directory.CreateDirectory(workRoot);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();

        await CleanupSqlAsync().ConfigureAwait(false);
        try
        {
            var primary = await SeedDeviceAsync(
                PrimarySiteId,
                PrimaryRegistrationId,
                PrimaryAgentId,
                "issue-157-device",
                "issue-157-key",
                DateTimeOffset.UtcNow.AddDays(1)).ConfigureAwait(false);
            var expired = await SeedDeviceAsync(
                ExpiredSiteId,
                ExpiredRegistrationId,
                ExpiredAgentId,
                "issue-157-expired-device",
                "issue-157-expired-key",
                DateTimeOffset.UtcNow.AddMinutes(-1)).ConfigureAwait(false);
            var sqlVersion = await ReadSqlServerVersionAsync().ConfigureAwait(false);
            var sqliteVersion = await ReadSqliteVersionAsync().ConfigureAwait(false);

            var d1 = await MeasureD1Async(client, primary, Path.Combine(workRoot, "d1")).ConfigureAwait(false);
            var d2 = await MeasureD2Async(client, primary, Path.Combine(workRoot, "d2")).ConfigureAwait(false);
            var d3 = await MeasureD3Async(client, primary, Path.Combine(workRoot, "d3")).ConfigureAwait(false);
            var d4 = await MeasureD4Async(client, primary, Path.Combine(workRoot, "d4")).ConfigureAwait(false);
            var d5 = await MeasureD5Async(client, primary, expired).ConfigureAwait(false);

            var evidence = new
            {
                Schema = "hvo-environmental-observation-delivery-performance",
                SchemaVersion = 1,
                Issue = 157,
                Candidate = new
                {
                    revision.CandidateSha,
                    revision.Branch,
                    revision.Dirty,
                    revision.DirtyFingerprintSha256
                },
                RecordedAtUtc = DateTimeOffset.UtcNow,
                RepeatCommand,
                Environment = new
                {
                    OperatingSystem = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Cpu = ReadCpuIdentity(),
                    LogicalProcessors = Environment.ProcessorCount,
                    MemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                    Sdk = RunCommand("dotnet", repositoryRoot, "--version"),
                    Runtime = RuntimeInformation.FrameworkDescription,
                    RuntimeVersion = Environment.Version.ToString(),
                    Configuration = "Release",
                    ServerGc = System.Runtime.GCSettings.IsServerGC,
                    Container = new
                    {
                        Fixture = "AssemblyHooks.Fixture SQL Server Testcontainer",
                        RuntimeVersion = TryRunCommand("docker", repositoryRoot, "version", "--format", "{{.Server.Version}}")
                    },
                    SqlServerVersion = sqlVersion,
                    SqliteVersion = sqliteVersion,
                    StorageType = "N/A: Testcontainers volumes and the temporary SQLite root use runner-managed storage; the harness does not infer physical media type."
                },
                Workload = new
                {
                    Method = "Production SQLite WAL outbox and delivery worker through a test-only authenticated in-process HTTP transport to the LogicHost receiver and fixture SQL Server.",
                    Determinism = "Versioned canonical observation JSON, deterministic GUID inputs, fixed timestamps, canonical content/source identities, and aggregate SHA-256 checks.",
                    D1 = new { Observations = D1ObservationCount, Providers = 10, D1BatchSize, D1WarmupBatches, SetupBatches = 65, D1MeasuredBatches },
                    D2 = new { Concurrency = new[] { 1, 4, 8 }, D2WarmupOperations, D2MeasuredOperations },
                    D3 = new { LogicalOutageSeconds = D3ObservationCount, ArrivalRateObservationsPerSecond = 1, D3ObservationCount, RestartAfterRecords = D3ObservationCount / 2 },
                    D4 = "Five deterministic durability boundaries around local commit, restart, HTTP send, central commit/response, and local acknowledgement.",
                    D5 = "Executed credential and binding cases are distinguished from required-gate references."
                },
                Baseline = new
                {
                    Result = "N/A",
                    Reason = "Issue #157 introduces the first end-to-end environmental delivery evidence run, so no same-path predecessor artifact exists.",
                    NearestArtifactOutboxShapeComparison = new
                    {
                        Reference = "ArtifactOutboxPerformanceTests.W2W3MAndW3P_OutageRecoveryEvidence",
                        Similar = "Both use a durable SQLite WAL outbox, bounded claims, restart recovery, retry settlement, backlog bytes, checksums, and drain-rate evidence.",
                        Different = "The nearest artifact path persists artifact manifests and object acknowledgements; this candidate persists canonical metadata observations and drains them through authenticated HTTP into relational environmental rows. No numeric baseline comparison is claimed."
                    }
                },
                D1 = d1,
                D2 = d2,
                D3 = d3,
                D4 = d4,
                D5 = d5,
                Correctness = new
                {
                    DeterministicIdentities = true,
                    CanonicalChecksumsVerified = true,
                    OneCentralLogicalRowPerIdentity = true,
                    DurableClaimLossOrCollision = false,
                    AcknowledgedLoss = false,
                    FinalBacklogRecords = 0,
                    Result = "Pass"
                },
                Result = new
                {
                    Outcome = "Pass",
                    PortableTimingBudget = "Only D3 recovery must exceed the declared arrival rate of one observation per second; all other timing values are evidence, not cross-machine ceilings.",
                    MeasurementLimitations = new
                    {
                        SqlLogicalIo = "N/A: the in-process HTTP receiver does not expose per-request SQL STATISTICS IO without altering production registration; durable row counts and payload checksums are recorded instead.",
                        NetworkWireBytes = "N/A: TestServer has no physical network interface; serialized HTTP entity request/response bytes are recorded.",
                        PerProcessIsolation = "N/A: CPU, allocation, and working-set measurements include the in-process test, CameraAgent.Common delivery code, TestServer LogicHost, and client HTTP serialization."
                    }
                }
            };

            var evidencePath = Path.Combine(evidenceDirectory, "environmental-observation-delivery-performance.json");
            var temporaryPath = string.Concat(evidencePath, ".tmp");
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(evidence, EvidenceJsonOptions)).ConfigureAwait(false);
            File.Move(temporaryPath, evidencePath, overwrite: true);
            TestContext.WriteLine("Issue #157 environmental delivery performance evidence generated.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
            await CleanupSqlAsync().ConfigureAwait(false);
        }
    }

    private static async Task<object> MeasureD1Async(HttpClient client, TestDevice device, string root)
    {
        Directory.CreateDirectory(root);
        var clock = new MutableTimeProvider(Epoch);
        using var outbox = new SqliteEnvironmentalObservationOutbox(clock);
        var measuredOutbox = new MeasuringOutbox(outbox);
        var batchSamples = new List<double>(D1MeasuredBatches);
        var allPayloadBytes = 0L;
        var measuredPayloadBytes = 0L;
        var allStarted = Stopwatch.GetTimestamp();
        var process = Process.GetCurrentProcess();
        var cpuStart = TimeSpan.Zero;
        var allocationsStart = 0L;
        var workingSetStart = 0L;
        var workingSetPeak = 0L;
        for (var batch = 0; batch < D1ObservationCount / D1BatchSize; batch++)
        {
            if (batch == D1ObservationCount / D1BatchSize - D1MeasuredBatches)
            {
                StabilizeGc();
                process.Refresh();
                cpuStart = process.TotalProcessorTime;
                allocationsStart = GC.GetTotalAllocatedBytes(precise: true);
                workingSetStart = process.WorkingSet64;
                workingSetPeak = workingSetStart;
            }
            var started = Stopwatch.GetTimestamp();
            for (var offset = 0; offset < D1BatchSize; offset++)
            {
                var index = batch * D1BatchSize + offset;
                var observation = CreateObservation(index, device, "d1", index % 10);
                var payloadBytes = EnvironmentalObservationJson.Serialize(observation).Length;
                var disposition = await measuredOutbox.EnqueueAsync(root, observation, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(EnvironmentalObservationEnqueueDisposition.Enqueued, disposition);
                clock.Advance(TimeSpan.FromSeconds(1));
                allPayloadBytes += payloadBytes;
                if (batch >= D1ObservationCount / D1BatchSize - D1MeasuredBatches)
                {
                    measuredPayloadBytes += payloadBytes;
                }
            }
            if (batch >= D1ObservationCount / D1BatchSize - D1MeasuredBatches)
            {
                batchSamples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                process.Refresh();
                workingSetPeak = Math.Max(workingSetPeak, process.WorkingSet64);
            }
        }
        var enqueueElapsed = Stopwatch.GetElapsedTime(allStarted);
        process.Refresh();
        var enqueueCpu = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
        var enqueueAllocations = GC.GetTotalAllocatedBytes(precise: true) - allocationsStart;
        var workingSetEnd = process.WorkingSet64;
        var snapshot = await outbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        var sqliteFiles = ReadSqliteFileBytes(root);
        Assert.AreEqual(D1ObservationCount, snapshot.PendingCount);
        Assert.AreEqual(allPayloadBytes, snapshot.PendingBytes);

        StabilizeGc();
        process.Refresh();
        var deliveryCpuStart = process.TotalProcessorTime;
        var deliveryAllocationsStart = GC.GetTotalAllocatedBytes(precise: true);
        var deliveryWorkingSetStart = process.WorkingSet64;
        var deliveryWorkingSetPeak = deliveryWorkingSetStart;
        using var transport = new AuthenticatedHttpTransport(client, device.DeviceId, device.DeviceKey);
        using var telemetry = new EnvironmentalObservationDeliveryTelemetry(new EnvironmentalObservationDeliveryState(), clock);
        var delivery = CreateDeliveryService(transport, measuredOutbox, telemetry, clock, root, D1BatchSize);
        var drainStarted = Stopwatch.GetTimestamp();
        for (var batch = 0; batch < D1ObservationCount / D1BatchSize; batch++)
        {
            if (batch == D1ObservationCount / D1BatchSize - D1MeasuredBatches)
            {
                transport.ResetLatencyMeasurements();
                measuredOutbox.ResetAcknowledgementMeasurements();
            }
            await delivery.DrainBatchAsync(root, DeliveryOptions(D1BatchSize), CancellationToken.None).ConfigureAwait(false);
            process.Refresh();
            deliveryWorkingSetPeak = Math.Max(deliveryWorkingSetPeak, process.WorkingSet64);
        }
        var drainElapsed = Stopwatch.GetElapsedTime(drainStarted);
        process.Refresh();
        var deliveryCpu = (process.TotalProcessorTime - deliveryCpuStart).TotalMilliseconds;
        var deliveryAllocations = GC.GetTotalAllocatedBytes(precise: true) - deliveryAllocationsStart;
        var deliveryWorkingSetEnd = process.WorkingSet64;
        var finalSnapshot = await outbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, finalSnapshot.StoredCount);
        Assert.AreEqual(D1ObservationCount, transport.RequestCount);
        Assert.AreEqual(D1MeasuredBatches * D1BatchSize, measuredOutbox.AcknowledgementDurationsMilliseconds.Count);

        var central = await ReadCentralEvidenceAsync(device, D1ObservationCount).ConfigureAwait(false);
        Assert.AreEqual(10, central.ProviderSources);
        var measuredSeconds = batchSamples.Sum() / 1_000d;
        return new
        {
            Enqueue = new
            {
                Observations = D1ObservationCount,
                Providers = 10,
                PayloadBytes = allPayloadBytes,
                MeasuredPayloadBytes = measuredPayloadBytes,
                WarmupBatches = D1WarmupBatches,
                SetupBatches = 65,
                MeasuredBatches = D1MeasuredBatches,
                BatchSize = D1BatchSize,
                MedianBatchMilliseconds = Percentile(batchSamples, 0.5),
                P95BatchMilliseconds = Percentile(batchSamples, 0.95),
                MeasuredObservationsPerSecond = D1MeasuredBatches * D1BatchSize / measuredSeconds,
                TotalElapsedMilliseconds = enqueueElapsed.TotalMilliseconds,
                CpuDeltaMilliseconds = enqueueCpu,
                TotalAllocationsDeltaBytes = enqueueAllocations,
                WorkingSetStartBytes = workingSetStart,
                WorkingSetEndBytes = workingSetEnd,
                WorkingSetPeakBytes = workingSetPeak
            },
            Io = new
            {
                sqliteFiles.DatabaseBytes,
                sqliteFiles.WalBytes,
                sqliteFiles.SharedMemoryBytes,
                TotalSqliteBytes = sqliteFiles.DatabaseBytes + sqliteFiles.WalBytes + sqliteFiles.SharedMemoryBytes,
                RequestEntityBytes = transport.RequestBytes,
                ResponseEntityBytes = transport.ResponseBytes,
                HttpRequestCount = transport.RequestCount,
                OutboxOperationCounts = new
                {
                    measuredOutbox.EnqueueCount,
                    measuredOutbox.ClaimCount,
                    measuredOutbox.AcknowledgeCount,
                    MinimumTransactionalCommits = measuredOutbox.EnqueueCount + measuredOutbox.ClaimCount + measuredOutbox.AcknowledgeCount
                },
                SqliteStatementCount = "N/A: Microsoft.Data.Sqlite does not expose a stable per-connection statement counter; production outbox API operations and minimum transactional commits are recorded.",
                SqliteBytesWritten = "N/A: Microsoft.Data.Sqlite does not expose physical bytes written through the managed provider; database, WAL, and shared-memory footprints are recorded.",
                SqliteCheckpointCount = "N/A: SQLite automatic WAL checkpoint counts are not exposed through Microsoft.Data.Sqlite.",
                SqliteFsyncCount = "N/A: fsync counts require operating-system tracing outside this in-process harness; synchronous=FULL is asserted by integration tests.",
                SqlOperationCount = "N/A: the production receiver is registered inside TestServer without a performance-only EF command interceptor; durable HTTP operations and resulting SQL rows are recorded.",
                SqlTransactionCount = "N/A: per-request SQL transaction counts are not exposed by the production receiver through TestServer.",
                SqlBytes = "N/A: SQL Server protocol bytes are not observable through the in-process TestServer transport.",
                SqlLogicalIo = "N/A: SQL STATISTICS IO is unavailable from the production receiver through TestServer without replacing its registered data path."
            },
            Delivery = new
            {
                WarmupBatches = D1WarmupBatches,
                SetupBatches = 65,
                MeasuredBatches = D1MeasuredBatches,
                BatchSize = D1BatchSize,
                SendDurationMedianMilliseconds = Percentile(transport.DurationsMilliseconds, 0.5),
                SendDurationP95Milliseconds = Percentile(transport.DurationsMilliseconds, 0.95),
                LocalAcknowledgementDurationMedianMilliseconds = Percentile(measuredOutbox.AcknowledgementDurationsMilliseconds, 0.5),
                LocalAcknowledgementDurationP95Milliseconds = Percentile(measuredOutbox.AcknowledgementDurationsMilliseconds, 0.95),
                DrainMilliseconds = drainElapsed.TotalMilliseconds,
                DrainObservationsPerSecond = D1ObservationCount / drainElapsed.TotalSeconds,
                EndToEndObservationsPerSecond = D1ObservationCount / (enqueueElapsed + drainElapsed).TotalSeconds,
                CpuDeltaMilliseconds = deliveryCpu,
                TotalAllocationsDeltaBytes = deliveryAllocations,
                WorkingSetStartBytes = deliveryWorkingSetStart,
                WorkingSetEndBytes = deliveryWorkingSetEnd,
                WorkingSetPeakBytes = deliveryWorkingSetPeak,
                DurableSqlRows = central.Rows,
                FinalBacklogRecords = finalSnapshot.StoredCount
            },
            Correctness = new
            {
                central.Rows,
                central.ProviderSources,
                central.OrderedContentSha256,
                central.OrderedSourceIdentitySha256,
                CanonicalPayloadHashesMatch = central.CanonicalPayloadHashesMatch,
                Result = "Pass"
            }
        };
    }

    private static async Task<IReadOnlyList<object>> MeasureD2Async(HttpClient client, TestDevice device, string root)
    {
        var evidence = new List<object>();
        foreach (var concurrency in new[] { 1, 4, 8 })
        {
            var directTransport = new AuthenticatedHttpTransport(client, device.DeviceId, device.DeviceKey);
            using (directTransport)
            {
                var warmup = Enumerable.Range(0, D2WarmupOperations)
                    .Select(index => CreateObservation(100_000 + concurrency * 10_000 + index / 2, device, $"d2-{concurrency}", 0))
                    .ToArray();
                await RunAtConcurrencyAsync(warmup, concurrency, directTransport.SendAsync).ConfigureAwait(false);
                directTransport.ResetMeasurements();
                var measured = Enumerable.Range(0, D2MeasuredOperations)
                    .Select(index => CreateObservation(110_000 + concurrency * 10_000 + index / 2, device, $"d2-{concurrency}", 1))
                    .ToArray();
                var started = Stopwatch.GetTimestamp();
                var results = await RunAtConcurrencyAsync(measured, concurrency, directTransport.SendAsync).ConfigureAwait(false);
                var elapsed = Stopwatch.GetElapsedTime(started);
                Assert.IsTrue(results.All(result => result.Disposition == EnvironmentalObservationTransportDisposition.Acknowledged));
                Assert.AreEqual(D2MeasuredOperations / 2, results.Count(result =>
                    result.Acknowledgement?.Disposition == EnvironmentalObservationDeliveryDisposition.Accepted));
                Assert.AreEqual(D2MeasuredOperations / 2, results.Count(result =>
                    result.Acknowledgement?.Disposition == EnvironmentalObservationDeliveryDisposition.Duplicate));

                var measuredRows = await CountObservationRangeAsync(
                    110_000 + concurrency * 10_000,
                    110_000 + concurrency * 10_000 + D2MeasuredOperations / 2 - 1).ConfigureAwait(false);
                Assert.AreEqual(D2MeasuredOperations / 2, measuredRows);

                var claimRoot = Path.Combine(root, concurrency.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(claimRoot);
                var clock = new MutableTimeProvider(Epoch);
                using var outbox = new SqliteEnvironmentalObservationOutbox(clock, maximumRecords: D2MeasuredOperations);
                var claimObservations = Enumerable.Range(0, D2MeasuredOperations)
                    .Select(index => CreateObservation(200_000 + concurrency * 10_000 + index, device, $"d2-claim-{concurrency}", 2))
                    .ToArray();
                foreach (var observation in claimObservations)
                {
                    Assert.AreEqual(
                        EnvironmentalObservationEnqueueDisposition.Enqueued,
                        await outbox.EnqueueAsync(claimRoot, observation, CancellationToken.None).ConfigureAwait(false));
                }
                using var claimTransport = new AuthenticatedHttpTransport(client, device.DeviceId, device.DeviceKey);
                var services = Enumerable.Range(0, concurrency).Select(_ =>
                {
                    var telemetry = new EnvironmentalObservationDeliveryTelemetry(new EnvironmentalObservationDeliveryState(), clock);
                    return new DeliveryOwner(CreateDeliveryService(claimTransport, outbox, telemetry, clock, claimRoot, 1), telemetry);
                }).ToArray();
                try
                {
                    for (var offset = 0; offset < D2MeasuredOperations; offset += concurrency)
                    {
                        var count = Math.Min(concurrency, D2MeasuredOperations - offset);
                        await Task.WhenAll(Enumerable.Range(0, count).Select(index =>
                            services[index].Service.DrainBatchAsync(
                                claimRoot,
                                DeliveryOptions(1),
                                CancellationToken.None).AsTask())).ConfigureAwait(false);
                    }
                }
                finally
                {
                    foreach (var owner in services)
                    {
                        owner.Telemetry.Dispose();
                    }
                }
                var claimSnapshot = await outbox.GetSnapshotAsync(claimRoot, CancellationToken.None).ConfigureAwait(false);
                var claimRows = await CountObservationRangeAsync(
                    200_000 + concurrency * 10_000,
                    200_000 + concurrency * 10_000 + D2MeasuredOperations - 1).ConfigureAwait(false);
                Assert.AreEqual(0, claimSnapshot.StoredCount);
                Assert.AreEqual(D2MeasuredOperations, claimTransport.RequestCount);
                Assert.AreEqual(D2MeasuredOperations, claimTransport.DistinctObservationCount);
                Assert.AreEqual(D2MeasuredOperations, claimRows);

                evidence.Add(new
                {
                    Concurrency = concurrency,
                    WarmupOperations = D2WarmupOperations,
                    MeasuredOperations = D2MeasuredOperations,
                    ExactDuplicatePairs = D2MeasuredOperations / 2,
                    DistinctMeasuredIdentities = D2MeasuredOperations / 2,
                    Accepted = results.Count(result => result.Acknowledgement?.Disposition == EnvironmentalObservationDeliveryDisposition.Accepted),
                    Duplicates = results.Count(result => result.Acknowledgement?.Disposition == EnvironmentalObservationDeliveryDisposition.Duplicate),
                    MedianMilliseconds = Percentile(directTransport.DurationsMilliseconds, 0.5),
                    P95Milliseconds = Percentile(directTransport.DurationsMilliseconds, 0.95),
                    OperationsPerSecond = D2MeasuredOperations / elapsed.TotalSeconds,
                    CentralLogicalRows = measuredRows,
                    DurableClaimOperations = D2MeasuredOperations,
                    DurableClaimHttpRequests = claimTransport.RequestCount,
                    DurableClaimDistinctIdentities = claimTransport.DistinctObservationCount,
                    DurableClaimCentralRows = claimRows,
                    FinalOutboxRecords = claimSnapshot.StoredCount,
                    ClaimLossOrCollision = false,
                    Result = "Pass"
                });
            }
        }
        return evidence;
    }

    private static async Task<object> MeasureD3Async(HttpClient client, TestDevice device, string root)
    {
        Directory.CreateDirectory(root);
        var clock = new MutableTimeProvider(Epoch);
        var outageAttempts = 0;
        using (var firstOutbox = new SqliteEnvironmentalObservationOutbox(clock, maximumRecords: D3ObservationCount))
        using (var firstTelemetry = new EnvironmentalObservationDeliveryTelemetry(new EnvironmentalObservationDeliveryState(), clock))
        {
            using var firstWorker = CreateDeliveryService(
                new AlwaysUnavailableTransport(),
                firstOutbox,
                firstTelemetry,
                clock,
                root,
                1);
            for (var index = 0; index < D3ObservationCount / 2; index++)
            {
                var observation = CreateObservation(300_000 + index, device, "d3", index % 10);
                await firstOutbox.EnqueueAsync(root, observation, CancellationToken.None).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromSeconds(1));
                if ((index + 1) % 150 == 0)
                {
                    await firstWorker.DrainBatchAsync(root, DeliveryOptions(1), CancellationToken.None).ConfigureAwait(false);
                    outageAttempts++;
                }
            }
        }
        SqliteConnection.ClearAllPools();
        using var restartedOutbox = new SqliteEnvironmentalObservationOutbox(clock, maximumRecords: D3ObservationCount);
        using var outageTelemetry = new EnvironmentalObservationDeliveryTelemetry(new EnvironmentalObservationDeliveryState(), clock);
        var restartedWorker = CreateDeliveryService(
            new AlwaysUnavailableTransport(),
            restartedOutbox,
            outageTelemetry,
            clock,
            root,
            1);
        for (var index = D3ObservationCount / 2; index < D3ObservationCount; index++)
        {
            var observation = CreateObservation(300_000 + index, device, "d3", index % 10);
            await restartedOutbox.EnqueueAsync(root, observation, CancellationToken.None).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromSeconds(1));
            if ((index + 1) % 150 == 0)
            {
                await restartedWorker.DrainBatchAsync(root, DeliveryOptions(1), CancellationToken.None).ConfigureAwait(false);
                outageAttempts++;
            }
        }
        var outageSnapshot = await restartedOutbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        var sqliteFiles = ReadSqliteFileBytes(root);
        Assert.AreEqual(D3ObservationCount, outageSnapshot.StoredCount);
        Assert.AreEqual(outageAttempts, outageSnapshot.RetryCount);
        Assert.IsNotNull(outageSnapshot.OldestPendingUtc);
        var oldestAge = outageSnapshot.EvaluatedUtc - outageSnapshot.OldestPendingUtc.Value;
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMinutes(10), oldestAge);

        clock.Advance(TimeSpan.FromSeconds(5));
        using var transport = new AuthenticatedHttpTransport(client, device.DeviceId, device.DeviceKey);
        using var recoveryTelemetry = new EnvironmentalObservationDeliveryTelemetry(new EnvironmentalObservationDeliveryState(), clock);
        var recovery = CreateDeliveryService(transport, restartedOutbox, recoveryTelemetry, clock, root, D3ObservationCount);
        var started = Stopwatch.GetTimestamp();
        await recovery.DrainBatchAsync(root, DeliveryOptions(D3ObservationCount), CancellationToken.None).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var finalSnapshot = await restartedOutbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        var centralRows = await CountObservationRangeAsync(300_000, 300_000 + D3ObservationCount - 1).ConfigureAwait(false);
        var drainRate = D3ObservationCount / elapsed.TotalSeconds;
        Assert.AreEqual(0, finalSnapshot.StoredCount);
        Assert.AreEqual(D3ObservationCount, centralRows);
        Assert.IsGreaterThan(1d, drainRate, "Outage recovery must exceed the declared one observation per second arrival rate.");
        return new
        {
            LogicalOutageSeconds = D3ObservationCount,
            ArrivalRateObservationsPerSecond = 1,
            QueuedRecords = outageSnapshot.StoredCount,
            QueuedPayloadBytes = outageSnapshot.StoredBytes,
            OldestAgeSeconds = oldestAge.TotalSeconds,
            RestartAfterRecords = D3ObservationCount / 2,
            RestartScope = "Disposed the first outbox and worker, cleared SQLite pools, and reconstructed both worker and outbox against physical WAL state.",
            OutageDeliveryAttempts = outageAttempts,
            PreRecoveryRetryRecords = outageSnapshot.RetryCount,
            SqliteDatabaseBytes = sqliteFiles.DatabaseBytes,
            SqliteWalBytes = sqliteFiles.WalBytes,
            SqliteSharedMemoryBytes = sqliteFiles.SharedMemoryBytes,
            RecoveryMilliseconds = elapsed.TotalMilliseconds,
            DrainObservationsPerSecond = drainRate,
            HttpRequests = transport.RequestCount,
            DurableSqlRows = centralRows,
            FinalBacklogRecords = finalSnapshot.StoredCount,
            Result = "Pass"
        };
    }

    private static async Task<object> MeasureD4Async(HttpClient client, TestDevice device, string root)
    {
        Directory.CreateDirectory(root);
        var clock = new MutableTimeProvider(Epoch);
        var observation = CreateObservation(400_000, device, "d4", 0);
        var failedBeforeLocalCommit = false;
        var failedAfterLocalCommit = false;
        var enqueueFaults = new EnqueueBoundaryFaultInjector { FailBeforeCommit = true };
        using (var durable = new SqliteEnvironmentalObservationOutbox(clock, faultInjector: enqueueFaults))
        {
            try
            {
                await durable.EnqueueAsync(root, observation, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
                failedBeforeLocalCommit = true;
            }
            Assert.AreEqual(0, (await durable.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).StoredCount);
            enqueueFaults.FailBeforeCommit = false;
            enqueueFaults.FailAfterCommit = true;
            try
            {
                await durable.EnqueueAsync(root, observation, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
                failedAfterLocalCommit = true;
            }
        }

        using var restarted = new SqliteEnvironmentalObservationOutbox(clock);
        var afterRestart = await restarted.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(failedBeforeLocalCommit);
        Assert.IsTrue(failedAfterLocalCommit);
        Assert.AreEqual(1, afterRestart.StoredCount);
        Assert.AreEqual(
            EnvironmentalObservationEnqueueDisposition.Duplicate,
            await restarted.EnqueueAsync(root, observation, CancellationToken.None).ConfigureAwait(false));
        using var realTransport = new AuthenticatedHttpTransport(client, device.DeviceId, device.DeviceKey);
        var faultTransport = new DeliveryBoundaryFaultTransport(realTransport);
        using var telemetry = new EnvironmentalObservationDeliveryTelemetry(new EnvironmentalObservationDeliveryState(), clock);
        var delivery = CreateDeliveryService(faultTransport, restarted, telemetry, clock, root, 1);

        await delivery.DrainBatchAsync(root, DeliveryOptions(1), CancellationToken.None).ConfigureAwait(false);
        var afterPreSendFailure = await restarted.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, afterPreSendFailure.RetryCount);
        Assert.AreEqual(0, realTransport.RequestCount);

        clock.Advance(TimeSpan.FromSeconds(5));
        await delivery.DrainBatchAsync(root, DeliveryOptions(1), CancellationToken.None).ConfigureAwait(false);
        var afterPostCommitFailure = await restarted.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, afterPostCommitFailure.RetryCount);
        Assert.AreEqual(1, realTransport.RequestCount);
        Assert.AreEqual(EnvironmentalObservationDeliveryDisposition.Accepted, faultTransport.LostAcknowledgement?.Disposition);

        clock.Advance(TimeSpan.FromSeconds(5));
        var postAcknowledgementFault = new ThrowAfterAcknowledgementOutbox(restarted);
        var postAcknowledgementDelivery = CreateDeliveryService(
            faultTransport,
            postAcknowledgementFault,
            telemetry,
            clock,
            root,
            1);
        var failedAfterAcknowledgement = false;
        try
        {
            await postAcknowledgementDelivery.DrainBatchAsync(
                root,
                DeliveryOptions(1),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            failedAfterAcknowledgement = true;
        }
        var afterAcknowledgement = await restarted.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(failedAfterAcknowledgement);
        Assert.AreEqual(0, afterAcknowledgement.StoredCount);
        Assert.AreEqual(EnvironmentalObservationDeliveryDisposition.Duplicate, faultTransport.FinalAcknowledgement?.Disposition);

        restarted.Dispose();
        using var afterAcknowledgementRestart = new SqliteEnvironmentalObservationOutbox(clock);
        var restartSnapshot = await afterAcknowledgementRestart.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        var centralRows = await CountObservationRangeAsync(400_000, 400_000).ConfigureAwait(false);
        Assert.AreEqual(0, restartSnapshot.StoredCount);
        Assert.AreEqual(1, centralRows);
        return new
        {
            BeforeLocalCommit = new { Executed = true, FailureObserved = failedBeforeLocalCommit, DurableRecords = 0 },
            AfterLocalCommitAndRestart = new
            {
                Executed = true,
                FailureObserved = failedAfterLocalCommit,
                DurableRecords = afterRestart.StoredCount,
                CallerRetryDisposition = "Duplicate"
            },
            BeforeHttpSend = new { Executed = true, FailureObserved = true, HttpRequests = 0, RetriedRecords = afterPreSendFailure.RetryCount },
            AfterCentralCommitBeforeResponse = new { Executed = true, FailureObserved = true, CentralDisposition = "Accepted", RetriedRecords = afterPostCommitFailure.RetryCount },
            AfterLocalAcknowledgement = new { Executed = true, FailureObserved = failedAfterAcknowledgement, Restarted = true, FinalBacklogRecords = restartSnapshot.StoredCount },
            CentralLogicalRows = centralRows,
            HttpRequests = realTransport.RequestCount,
            DeterministicConvergence = true,
            AcknowledgedLoss = false,
            Result = "Pass"
        };
    }

    private static async Task<object> MeasureD5Async(HttpClient client, TestDevice valid, TestDevice expired)
    {
        using var validTransport = new AuthenticatedHttpTransport(client, valid.DeviceId, valid.DeviceKey);
        var validResult = await validTransport.SendAsync(
            CreateObservation(500_000, valid, "d5-valid", 0),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Acknowledged, validResult.Disposition);

        var spoofedSite = await validTransport.SendAsync(
            CreateObservation(500_001, valid, "d5-spoof-site", 0) with
            {
                Target = new EnvironmentalObservationTarget(DeterministicGuid(1_100_000), valid.AgentId)
            },
            CancellationToken.None).ConfigureAwait(false);
        var spoofedDevice = await validTransport.SendAsync(
            CreateObservation(500_002, valid, "d5-spoof-device", 0) with
            {
                Target = new EnvironmentalObservationTarget(valid.SiteId, DeterministicGuid(1_100_001))
            },
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Terminal, spoofedSite.Disposition);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Terminal, spoofedDevice.Disposition);

        using var expiredTransport = new AuthenticatedHttpTransport(client, expired.DeviceId, expired.DeviceKey);
        var expiredResult = await expiredTransport.SendAsync(
            CreateObservation(500_003, expired, "d5-expired", 0),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.AuthenticationBlocked, expiredResult.Disposition);

        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == valid.RegistrationId)
                .ConfigureAwait(false);
            db.CentralFrames.Add(new CentralFrame
            {
                RegistrationId = registration.Id,
                DevicePublicId = valid.AgentId,
                ObservatoryId = valid.SiteId,
                AgentId = valid.AgentId.ToString("D", CultureInfo.InvariantCulture),
                FrameId = DeterministicGuid(1_000_009),
                CapturedAtUtc = Epoch,
                FirstReceivedAtUtc = Epoch
            });
            var movedSite = new Observatory
            {
                Id = MovedSiteId,
                OwnerUserId = "issue-157-performance",
                Name = "Issue 157 moved performance site",
                TimeZoneId = "UTC",
                CreatedAtUtc = Epoch
            };
            db.Observatories.Add(movedSite);
            registration.Observatory = movedSite;
            registration.ObservatoryId = movedSite.Id;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        var historicalMovedSite = await validTransport.SendAsync(
            CreateObservation(500_004, valid, "d5-moved", 0),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Acknowledged, historicalMovedSite.Disposition);

        const string reprovisionedKey = "issue-157-reprovisioned-key";
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var previous = await db.DeviceRegistrations.SingleAsync(item => item.Id == valid.RegistrationId)
                .ConfigureAwait(false);
            previous.Status = DeviceRegistrationStatus.Revoked;
            previous.RevokedReason = "reprovisioned";
            db.DeviceRegistrations.Add(new DeviceRegistration
            {
                Id = ReprovisionedRegistrationId,
                DeviceId = valid.DeviceId,
                ObservatoryId = MovedSiteId,
                FriendlyName = "Issue 157 reprovisioned device",
                ObservatoryName = "Issue 157 moved performance site",
                OwnerUserId = "issue-157-performance",
                OwnerDisplayName = "Performance Harness",
                Status = DeviceRegistrationStatus.Active,
                VerificationCodeHash = new string('B', 64),
                DevicePublicId = ReprovisionedAgentId,
                DeviceKeyHash = DeviceRegistrationService.ComputeSha256(reprovisionedKey),
                IssuedAtUtc = Epoch.AddHours(1),
                ActivatedAtUtc = Epoch.AddHours(1),
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1)
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        using var reprovisionedTransport = new AuthenticatedHttpTransport(
            client,
            valid.DeviceId,
            reprovisionedKey);
        var historicalAgent = CreateObservation(
            500_005,
            new TestDevice(
                valid.DeviceId,
                reprovisionedKey,
                MovedSiteId,
                ReprovisionedRegistrationId,
                ReprovisionedAgentId),
            "d5-reprovisioned",
            0) with
        {
            Target = new EnvironmentalObservationTarget(MovedSiteId, valid.AgentId)
        };
        var reprovisionedResult = await reprovisionedTransport.SendAsync(
            historicalAgent,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Acknowledged, reprovisionedResult.Disposition);
        return new
        {
            Executed = new object[]
            {
                new { Case = "Valid active credential and bound target", Result = "Acknowledged" },
                new { Case = "Spoofed site binding", Result = "Rejected" },
                new { Case = "Spoofed device binding", Result = "Rejected" },
                new { Case = "Expired credential", Result = "Rejected" },
                new { Case = "Historical moved-site binding", Result = "Acknowledged" },
                new { Case = "Reprovisioned device historical agent binding without frame or rig evidence", Result = "Acknowledged" }
            },
            Result = "Pass"
        };
    }

    private static EnvironmentalObservationDeliveryService CreateDeliveryService(
        IEnvironmentalObservationTransport transport,
        IEnvironmentalObservationOutbox outbox,
        EnvironmentalObservationDeliveryTelemetry telemetry,
        TimeProvider clock,
        string root,
        int batchSize)
        => new(
            transport,
            NullEnvironmentalObservationTargetResolver.Instance,
            outbox,
            new EnvironmentalObservationDeliveryWakeup(),
            new EnvironmentalObservationDeliveryState(),
            telemetry,
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalDelivery = DeliveryOptions(batchSize)
            }),
            clock,
            NullLogger<EnvironmentalObservationDeliveryService>.Instance);

    private sealed class NullEnvironmentalObservationTargetResolver : IEnvironmentalObservationTargetResolver
    {
        public static NullEnvironmentalObservationTargetResolver Instance { get; } = new();

        public ValueTask<EnvironmentalObservationResolvedTarget?> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<EnvironmentalObservationResolvedTarget?>(null);
    }

    private static EnvironmentalObservationDeliveryOptions DeliveryOptions(int batchSize)
        => new()
        {
            BatchSize = batchSize,
            LeaseSeconds = 120,
            RequestTimeoutSeconds = 60,
            RetryInitialDelaySeconds = 5,
            RetryMaximumDelaySeconds = 5,
            MaximumAttempts = 10
        };

    private static EnvironmentalObservationV1 CreateObservation(
        int identity,
        TestDevice device,
        string scenario,
        int provider)
    {
        var parameters = Json($$"""{"scenario":"{{scenario}}","provider":{{provider}}}""");
        var observed = Epoch.AddSeconds(identity);
        return new EnvironmentalObservationV1(
            EnvironmentalObservationV1.CurrentSchemaVersion,
            DeterministicGuid(identity),
            new EnvironmentalObservationTarget(device.SiteId, device.AgentId),
            new EnvironmentalObservationSource(
                $"provider-{provider}",
                $"{scenario}-source-{provider}",
                "1.0.0",
                EnvironmentalObservationSourceKind.Measured,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity("issue-157-normalizer", "1.0.0"),
                    parameters,
                    CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            observed,
            null,
            null,
            observed.AddMinutes(-1),
            observed.AddMinutes(5),
            observed.AddMinutes(3),
            new EnvironmentalObservationValue(
                EnvironmentalObservationKind.RelativeHumidity,
                EnvironmentalObservationUnit.Percent,
                30 + identity % 60,
                null,
                EnvironmentalObservationQuality.Good,
                0.5),
            []);
    }

    private static async Task<TestDevice> SeedDeviceAsync(
        Guid siteId,
        Guid registrationId,
        Guid agentId,
        string deviceId,
        string deviceKey,
        DateTimeOffset expiresAtUtc)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var site = new Observatory
        {
            Id = siteId,
            OwnerUserId = "issue-157-performance",
            Name = "Issue 157 performance site",
            TimeZoneId = "UTC",
            CreatedAtUtc = Epoch
        };
        db.Observatories.Add(site);
        db.DeviceRegistrations.Add(new DeviceRegistration
        {
            Id = registrationId,
            DeviceId = deviceId,
            Observatory = site,
            ObservatoryId = siteId,
            FriendlyName = "Issue 157 performance device",
            ObservatoryName = site.Name,
            OwnerUserId = site.OwnerUserId,
            OwnerDisplayName = "Performance Harness",
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = new string('A', 64),
            DevicePublicId = agentId,
            DeviceKeyHash = DeviceRegistrationService.ComputeSha256(deviceKey),
            IssuedAtUtc = Epoch,
            ActivatedAtUtc = Epoch,
            ExpiresAtUtc = expiresAtUtc
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new(deviceId, deviceKey, siteId, registrationId, agentId);
    }

    private static async Task CleanupSqlAsync()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.EnvironmentalObservationSources
            .Where(source => IsolatedSiteIds.Contains(source.SiteId))
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);
        await db.CentralFrames
            .Where(frame => IsolatedSiteIds.Contains(frame.ObservatoryId))
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);
        await db.DeviceRegistrations
            .Where(registration => IsolatedSiteIds.Contains(registration.ObservatoryId))
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);
        await db.Observatories
            .Where(site => IsolatedSiteIds.Contains(site.Id))
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);
    }

    private static async Task<CentralEvidence> ReadCentralEvidenceAsync(
        TestDevice device,
        int expectedRows)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.EnvironmentalObservations
            .AsNoTracking()
            .Where(row => row.SiteId == device.SiteId && row.AgentId == device.AgentId &&
                row.Source!.SourceId.StartsWith("d1-source-"))
            .Select(row => new { row.ObservationId, row.PayloadSha256, row.SourceIdentitySha256 })
            .ToArrayAsync()
            .ConfigureAwait(false);
        Assert.AreEqual(expectedRows, rows.Length);
        var expectedHashes = Enumerable.Range(0, expectedRows)
            .Select(index => CreateObservation(index, device, "d1", index % 10))
            .ToDictionary(
                observation => observation.ObservationId,
                EnvironmentalObservationJson.ComputeContentSha256);
        var hashesMatch = rows.All(row => expectedHashes.TryGetValue(row.ObservationId, out var expected) &&
            string.Equals(expected, row.PayloadSha256, StringComparison.Ordinal));
        Assert.IsTrue(hashesMatch);
        var orderedContent = rows.OrderBy(row => row.ObservationId).Select(row => row.PayloadSha256);
        var orderedSources = rows.OrderBy(row => row.SourceIdentitySha256, StringComparer.Ordinal)
            .Select(row => row.SourceIdentitySha256)
            .Distinct(StringComparer.Ordinal);
        return new(
            rows.Length,
            rows.Select(row => row.SourceIdentitySha256).Distinct(StringComparer.Ordinal).Count(),
            AggregateSha256(orderedContent),
            AggregateSha256(orderedSources),
            hashesMatch);
    }

    private static async Task<int> CountObservationRangeAsync(int firstIdentity, int lastIdentity)
    {
        var ids = Enumerable.Range(firstIdentity, lastIdentity - firstIdentity + 1)
            .Select(DeterministicGuid)
            .ToArray();
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.EnvironmentalObservations.CountAsync(row => ids.Contains(row.ObservationId)).ConfigureAwait(false);
    }

    private static async Task<EnvironmentalObservationTransportResult[]> RunAtConcurrencyAsync(
        EnvironmentalObservationV1[] inputs,
        int concurrency,
        Func<EnvironmentalObservationV1, CancellationToken, ValueTask<EnvironmentalObservationTransportResult>> operation)
    {
        var results = new EnvironmentalObservationTransportResult[inputs.Length];
        for (var offset = 0; offset < inputs.Length; offset += concurrency)
        {
            var count = Math.Min(concurrency, inputs.Length - offset);
            await Task.WhenAll(Enumerable.Range(0, count).Select(async index =>
            {
                results[offset + index] = await operation(inputs[offset + index], CancellationToken.None).ConfigureAwait(false);
            })).ConfigureAwait(false);
        }
        return results;
    }

    private static SqliteFileEvidence ReadSqliteFileBytes(string root)
    {
        var directory = Path.Combine(root, ".environment");
        var database = Directory.EnumerateFiles(directory, "*.db", SearchOption.TopDirectoryOnly).Single();
        return new(
            new FileInfo(database).Length,
            File.Exists(string.Concat(database, "-wal")) ? new FileInfo(string.Concat(database, "-wal")).Length : 0,
            File.Exists(string.Concat(database, "-shm")) ? new FileInfo(string.Concat(database, "-shm")).Length : 0);
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        Assert.IsNotEmpty(ordered);
        return ordered[Math.Max(0, (int)Math.Ceiling(ordered.Length * percentile) - 1)];
    }

    private static string AggregateSha256(IEnumerable<string> values)
        => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Concat(values))));

    private static Guid DeterministicGuid(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        bytes[15] = 157;
        return new Guid(bytes);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void StabilizeGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static RevisionEvidence ReadRevision(string repositoryRoot)
    {
        var candidate = RunGit(repositoryRoot, "rev-parse", "HEAD");
        var branch = RunGit(repositoryRoot, "branch", "--show-current");
        var dirtyStatus = RunGit(repositoryRoot, "status", "--porcelain");
        var fingerprintInput = new StringBuilder(dirtyStatus)
            .Append('\n')
            .Append(RunGit(repositoryRoot, "diff", "--binary", "HEAD"));
        var untracked = RunGit(repositoryRoot, "ls-files", "--others", "--exclude-standard", "-z")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal);
        foreach (var relativePath in untracked)
        {
            fingerprintInput.Append('\n').Append(relativePath).Append(':').Append(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(repositoryRoot, relativePath)))));
        }
        return new(
            candidate,
            branch,
            !string.IsNullOrWhiteSpace(dirtyStatus),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput.ToString()))));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
        => RunCommand("git", workingDirectory, arguments);

    private static string RunCommand(string executable, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? output.Trim()
            : throw new InvalidOperationException($"{executable} {string.Join(' ', arguments)} failed: {error.Trim()}");
    }

    private static string TryRunCommand(string executable, string workingDirectory, params string[] arguments)
    {
        try
        {
            return RunCommand(executable, workingDirectory, arguments);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"N/A: {executable} runtime version was unavailable to the harness ({exception.GetType().Name}).";
        }
    }

    private static async Task<string> ReadSqlServerVersionAsync()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'))";
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture) ?? "unknown";
    }

    private static async Task<string> ReadSqliteVersionAsync()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture) ?? "unknown";
    }

    private static string ReadCpuIdentity()
    {
        const string cpuInfoPath = "/proc/cpuinfo";
        if (File.Exists(cpuInfoPath))
        {
            var model = File.ReadLines(cpuInfoPath)
                .FirstOrDefault(line => line.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
            if (model is not null)
            {
                var separator = model.IndexOf(':', StringComparison.Ordinal);
                return separator >= 0 ? model[(separator + 1)..].Trim() : model.Trim();
            }
        }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "N/A: CPU identity unavailable on this platform.";
    }

    private sealed class AuthenticatedHttpTransport(HttpClient client, string deviceId, string deviceKey)
        : IEnvironmentalObservationTransport, IDisposable
    {
        private readonly ConcurrentBag<double> _durations = [];
        private readonly ConcurrentDictionary<Guid, byte> _observationIds = [];
        private long _requestBytes;
        private long _responseBytes;
        private int _requestCount;

        public IReadOnlyCollection<double> DurationsMilliseconds => _durations;
        public long RequestBytes => Interlocked.Read(ref _requestBytes);
        public long ResponseBytes => Interlocked.Read(ref _responseBytes);
        public int RequestCount => Volatile.Read(ref _requestCount);
        public int DistinctObservationCount => _observationIds.Count;

        public async ValueTask<EnvironmentalObservationTransportResult> SendAsync(
            EnvironmentalObservationV1 observation,
            CancellationToken cancellationToken)
        {
            var payload = EnvironmentalObservationDeliveryJson.Serialize(new EnvironmentalObservationDeliveryEnvelope(
                EnvironmentalObservationDeliveryEnvelope.CurrentSchemaVersion,
                deviceId,
                deviceKey,
                observation));
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            var started = Stopwatch.GetTimestamp();
            using var response = await client.PostAsync(
                new Uri("/api/device/environmental-observations", UriKind.Relative),
                content,
                cancellationToken).ConfigureAwait(false);
            var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            _durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Interlocked.Add(ref _requestBytes, payload.Length);
            Interlocked.Add(ref _responseBytes, responseBytes.Length);
            Interlocked.Increment(ref _requestCount);
            _observationIds.TryAdd(observation.ObservationId, 0);
            if (response.IsSuccessStatusCode)
            {
                var parsed = EnvironmentalObservationDeliveryJson.ParseAcknowledgement(responseBytes);
                if (!parsed.Validation.IsValid || parsed.Value is null ||
                    !EnvironmentalObservationDeliveryJson.Matches(parsed.Value, observation))
                {
                    return new(EnvironmentalObservationTransportDisposition.Quarantine, "invalid-acknowledgement");
                }
                return new(
                    EnvironmentalObservationTransportDisposition.Acknowledged,
                    parsed.Value.Disposition == EnvironmentalObservationDeliveryDisposition.Accepted ? "accepted" : "duplicate",
                    parsed.Value);
            }
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new(EnvironmentalObservationTransportDisposition.AuthenticationBlocked, "credentials-rejected"),
                HttpStatusCode.Conflict =>
                    new(EnvironmentalObservationTransportDisposition.Terminal, "http-409"),
                HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError =>
                    new(EnvironmentalObservationTransportDisposition.Retry, $"http-{(int)response.StatusCode}"),
                _ => new(EnvironmentalObservationTransportDisposition.Quarantine, $"http-{(int)response.StatusCode}")
            };
        }

        public void ResetMeasurements()
        {
            ResetLatencyMeasurements();
            _observationIds.Clear();
            Interlocked.Exchange(ref _requestBytes, 0);
            Interlocked.Exchange(ref _responseBytes, 0);
            Interlocked.Exchange(ref _requestCount, 0);
        }

        public void ResetLatencyMeasurements()
        {
            while (_durations.TryTake(out _))
            {
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class MeasuringOutbox(IEnvironmentalObservationOutbox inner) : IEnvironmentalObservationOutbox
    {
        private readonly ConcurrentBag<double> _acknowledgementDurations = [];
        private int _enqueueCount;
        private int _claimCount;
        private int _acknowledgeCount;
        public ConcurrentBag<double> AcknowledgementDurationsMilliseconds => _acknowledgementDurations;
        public int EnqueueCount => Volatile.Read(ref _enqueueCount);
        public int ClaimCount => Volatile.Read(ref _claimCount);
        public int AcknowledgeCount => Volatile.Read(ref _acknowledgeCount);

        public void ResetAcknowledgementMeasurements()
        {
            while (_acknowledgementDurations.TryTake(out _))
            {
            }
        }

        public async ValueTask<EnvironmentalObservationEnqueueDisposition> EnqueueAsync(string root, EnvironmentalObservationV1 observation, CancellationToken cancellationToken)
        {
            var result = await inner.EnqueueAsync(root, observation, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _enqueueCount);
            return result;
        }
        public async ValueTask<EnvironmentalObservationOutboxLease?> ClaimAsync(string root, string owner, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            var result = await inner.ClaimAsync(root, owner, leaseDuration, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _claimCount);
            return result;
        }
        public async ValueTask AcknowledgeAsync(string root, EnvironmentalObservationOutboxLease lease, EnvironmentalObservationAcknowledgement acknowledgement, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            await inner.AcknowledgeAsync(root, lease, acknowledgement, cancellationToken).ConfigureAwait(false);
            _acknowledgementDurations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Interlocked.Increment(ref _acknowledgeCount);
        }
        public ValueTask RetryAsync(string root, EnvironmentalObservationOutboxLease lease, DateTimeOffset retryAtUtc, string reason, CancellationToken cancellationToken)
            => inner.RetryAsync(root, lease, retryAtUtc, reason, cancellationToken);
        public ValueTask QuarantineAsync(string root, EnvironmentalObservationOutboxLease lease, string reason, CancellationToken cancellationToken)
            => inner.QuarantineAsync(root, lease, reason, cancellationToken);
        public ValueTask TerminalAsync(string root, EnvironmentalObservationOutboxLease lease, string reason, CancellationToken cancellationToken)
            => inner.TerminalAsync(root, lease, reason, cancellationToken);
        public ValueTask<EnvironmentalObservationOutboxSnapshot> GetSnapshotAsync(string root, CancellationToken cancellationToken)
            => inner.GetSnapshotAsync(root, cancellationToken);
        public ValueTask<IReadOnlyList<EnvironmentalObservationDeadLetter>> ReadDeadLettersAsync(string root, int maximumResults, CancellationToken cancellationToken)
            => inner.ReadDeadLettersAsync(root, maximumResults, cancellationToken);
        public ValueTask ReplayAsync(string root, long recordId, string actor, string reason, CancellationToken cancellationToken)
            => inner.ReplayAsync(root, recordId, actor, reason, cancellationToken);
        public ValueTask AbandonAsync(string root, long recordId, string actor, string reason, CancellationToken cancellationToken)
            => inner.AbandonAsync(root, recordId, actor, reason, cancellationToken);
    }

    private sealed class EnqueueBoundaryFaultInjector : IEnvironmentalObservationOutboxFaultInjector
    {
        public bool FailBeforeCommit { get; set; }
        public bool FailAfterCommit { get; set; }

        public ValueTask BeforeEnqueueCommitAsync(CancellationToken cancellationToken)
            => FailBeforeCommit
                ? ValueTask.FromException(new IOException("Injected failure before local commit."))
                : ValueTask.CompletedTask;

        public ValueTask AfterEnqueueCommitAsync(CancellationToken cancellationToken)
            => FailAfterCommit
                ? ValueTask.FromException(new IOException("Injected failure after local commit."))
                : ValueTask.CompletedTask;
    }

    private sealed class ThrowAfterAcknowledgementOutbox(IEnvironmentalObservationOutbox inner) : IEnvironmentalObservationOutbox
    {
        public ValueTask<EnvironmentalObservationEnqueueDisposition> EnqueueAsync(string root, EnvironmentalObservationV1 observation, CancellationToken cancellationToken)
            => inner.EnqueueAsync(root, observation, cancellationToken);
        public ValueTask<EnvironmentalObservationOutboxLease?> ClaimAsync(string root, string owner, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => inner.ClaimAsync(root, owner, leaseDuration, cancellationToken);
        public async ValueTask AcknowledgeAsync(string root, EnvironmentalObservationOutboxLease lease, EnvironmentalObservationAcknowledgement acknowledgement, CancellationToken cancellationToken)
        {
            await inner.AcknowledgeAsync(root, lease, acknowledgement, cancellationToken).ConfigureAwait(false);
            throw new IOException("Injected failure after local acknowledgement commit.");
        }
        public ValueTask RetryAsync(string root, EnvironmentalObservationOutboxLease lease, DateTimeOffset retryAtUtc, string reason, CancellationToken cancellationToken)
            => inner.RetryAsync(root, lease, retryAtUtc, reason, cancellationToken);
        public ValueTask QuarantineAsync(string root, EnvironmentalObservationOutboxLease lease, string reason, CancellationToken cancellationToken)
            => inner.QuarantineAsync(root, lease, reason, cancellationToken);
        public ValueTask TerminalAsync(string root, EnvironmentalObservationOutboxLease lease, string reason, CancellationToken cancellationToken)
            => inner.TerminalAsync(root, lease, reason, cancellationToken);
        public ValueTask<EnvironmentalObservationOutboxSnapshot> GetSnapshotAsync(string root, CancellationToken cancellationToken)
            => inner.GetSnapshotAsync(root, cancellationToken);
        public ValueTask<IReadOnlyList<EnvironmentalObservationDeadLetter>> ReadDeadLettersAsync(string root, int maximumResults, CancellationToken cancellationToken)
            => inner.ReadDeadLettersAsync(root, maximumResults, cancellationToken);
        public ValueTask ReplayAsync(string root, long recordId, string actor, string reason, CancellationToken cancellationToken)
            => inner.ReplayAsync(root, recordId, actor, reason, cancellationToken);
        public ValueTask AbandonAsync(string root, long recordId, string actor, string reason, CancellationToken cancellationToken)
            => inner.AbandonAsync(root, recordId, actor, reason, cancellationToken);
    }

    private sealed class AlwaysUnavailableTransport : IEnvironmentalObservationTransport
    {
        public ValueTask<EnvironmentalObservationTransportResult> SendAsync(
            EnvironmentalObservationV1 observation,
            CancellationToken cancellationToken)
            => ValueTask.FromException<EnvironmentalObservationTransportResult>(
                new HttpRequestException("Injected logical outage before HTTP send."));
    }

    private sealed class DeliveryBoundaryFaultTransport(AuthenticatedHttpTransport inner) : IEnvironmentalObservationTransport
    {
        private int _attempt;
        public EnvironmentalObservationAcknowledgement? LostAcknowledgement { get; private set; }
        public EnvironmentalObservationAcknowledgement? FinalAcknowledgement { get; private set; }

        public async ValueTask<EnvironmentalObservationTransportResult> SendAsync(
            EnvironmentalObservationV1 observation,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempt);
            if (attempt == 1)
            {
                throw new HttpRequestException("Injected failure before HTTP send.");
            }
            var result = await inner.SendAsync(observation, cancellationToken).ConfigureAwait(false);
            if (attempt == 2)
            {
                LostAcknowledgement = result.Acknowledgement;
                throw new HttpRequestException("Injected response loss after central commit.");
            }
            FinalAcknowledgement = result.Acknowledgement;
            return result;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed record TestDevice(
        string DeviceId,
        string DeviceKey,
        Guid SiteId,
        Guid RegistrationId,
        Guid AgentId);
    private sealed record RevisionEvidence(string CandidateSha, string Branch, bool Dirty, string DirtyFingerprintSha256);
    private sealed record SqliteFileEvidence(long DatabaseBytes, long WalBytes, long SharedMemoryBytes);
    private sealed record CentralEvidence(
        int Rows,
        int ProviderSources,
        string OrderedContentSha256,
        string OrderedSourceIdentitySha256,
        bool CanonicalPayloadHashesMatch);
    private sealed record DeliveryOwner(
        EnvironmentalObservationDeliveryService Service,
        EnvironmentalObservationDeliveryTelemetry Telemetry);
}
