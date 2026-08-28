using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Upload;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Microsoft.Data.Sqlite field getters read buffered row values.")]
public sealed class ArtifactOutboxPerformanceTests
{
    private const int W3MetadataCount = 10_000;
    private const int W3PayloadCount = 100;
    private const int CanonicalPayloadBytes = 12_879_360;
    private const long MaximumRssGrowthBytes = 64L * 1024 * 1024;
    private static readonly DateTimeOffset StartUtc = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly string[] QuarantineActions = ["quarantine", "abandon"];
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [TestMethod]
    public async Task W2W3MAndW3P_OutageRecoveryEvidence()
    {
        var revision = ReadEvidenceRevision();
        var repositoryRoot = GetRepositoryRoot();
        var outputDirectory = Path.Combine(repositoryRoot, "TestResults", "issue-97", revision);
        var workRoot = Path.Combine(outputDirectory, "performance-work");
        Directory.CreateDirectory(outputDirectory);
        if (Directory.Exists(workRoot))
        {
            Directory.Delete(workRoot, recursive: true);
        }
        Directory.CreateDirectory(workRoot);

        try
        {
            var payloadRelativePath = "payload/canonical-w2.bin";
            var payloadPath = Path.Combine(workRoot, "w3p", payloadRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var payloadSha256 = await WriteDeterministicPayloadAsync(payloadPath).ConfigureAwait(false);
            Assert.AreEqual(CanonicalPayloadBytes, new FileInfo(payloadPath).Length);
            Assert.AreEqual(payloadSha256, await ComputeSha256Async(payloadPath).ConfigureAwait(false));

            var w3m = await MeasureW3MetadataAsync(
                Path.Combine(workRoot, "w3m"), payloadPath, payloadSha256).ConfigureAwait(false);
            var w3p = await MeasureW3PayloadAsync(
                Path.Combine(workRoot, "w3p"), payloadRelativePath, payloadSha256).ConfigureAwait(false);

            var evidence = new
            {
                Revision = revision,
                Environment = new
                {
                    OperatingSystem = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Framework = RuntimeInformation.FrameworkDescription,
                    RuntimeVersion = Environment.Version.ToString(),
                    Sdk = ReadPinnedSdkVersion(repositoryRoot),
                    Configuration = BuildConfiguration,
                    Environment.ProcessorCount,
                    ServerGc = System.Runtime.GCSettings.IsServerGC,
                    SqliteVersion = await ReadSqliteVersionAsync().ConfigureAwait(false),
                    ExecutionCommand = "DOTNET_gcServer=1 HVO_EVIDENCE_REVISION=<revision> dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --no-build --configuration Release --filter FullyQualifiedName~ArtifactOutboxPerformanceTests.W2W3MAndW3P_OutageRecoveryEvidence"
                },
                Workload = new
                {
                    Ids = new[] { "W2", "W3M", "W3P" },
                    PayloadSource = "Deterministic byte sequence ((offset * 31) + 17) modulo 251",
                    PayloadBytes = CanonicalPayloadBytes,
                    PayloadSha256 = payloadSha256,
                    W3MetadataCount,
                    W3PayloadCount,
                    OutageCycles = 2,
                    ExpectedAttemptsPerRecoveredRecord = 3,
                    Concurrency = 1,
                    MinimumRecoveryCapturesPerSecond = 10.0,
                    MaximumRssGrowthBytes
                },
                W3M = w3m,
                W3P = w3p,
                Correctness = new
                {
                    PayloadLengthVerified = true,
                    PayloadChecksumVerifiedBeforeAndAfterDrain = true,
                    PersistedAttemptsVerifiedAfterEachReopen = true,
                    IndexedClaimPlanVerified = true,
                    StructuredAcknowledgementsPersisted = W3PayloadCount,
                    QuarantineIsolationStatusCode = 409,
                    FinalHeldBacklog = 0,
                    Result = "Two complete 503 cycles persisted their attempts, the reopened outbox drained above one capture per second, exact acknowledgements converged, and one isolated 409 was quarantined then abandoned through the audited operator path."
                }
            };

            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "artifact-outbox-performance.json"),
                JsonSerializer.Serialize(evidence, EvidenceOptions)).ConfigureAwait(false);
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

    private static async Task<W3MetadataMeasurement> MeasureW3MetadataAsync(
        string root,
        string sourcePayloadPath,
        string payloadSha256)
    {
        Directory.CreateDirectory(root);
        var clock = new MutableTimeProvider(StartUtc);
        var databasePath = DatabasePath(root);
        StabilizeGc();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = ReadCpuTime();
        var rssBefore = Environment.WorkingSet;
        double enqueueMilliseconds;
        double firstClaimMilliseconds;
        string firstKey;
        IReadOnlyList<string> queryPlan;
        DatabaseEvidence beforeReopen;

        using (var outbox = new SqliteArtifactOutbox(clock))
        {
            await outbox.InitializeAsync(root, CancellationToken.None).ConfigureAwait(false);
            using var observer = OpenDatabase(databasePath);
            await observer.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            var enqueueStarted = Stopwatch.GetTimestamp();
            for (var index = 0; index < W3MetadataCount; index++)
            {
                var manifest = CreateManifest(
                    root,
                    sourcePayloadPath,
                    "issue-97-w3m",
                    index,
                    $"metadata/{index:D5}.bin",
                    payloadSha256,
                    CanonicalPayloadBytes);
                await outbox.EnqueueAsync(root, manifest, CancellationToken.None).ConfigureAwait(false);
            }
            enqueueMilliseconds = Stopwatch.GetElapsedTime(enqueueStarted).TotalMilliseconds;

            queryPlan = await ReadClaimQueryPlanAsync(observer).ConfigureAwait(false);
            Assert.IsTrue(
                queryPlan.Any(static detail => detail.Contains("ix_artifact_outbox_claim", StringComparison.OrdinalIgnoreCase)),
                string.Join(Environment.NewLine, queryPlan));

            var claimStarted = Stopwatch.GetTimestamp();
            var firstLease = await outbox.ClaimAsync(
                root, "issue-97-w3m-first", LeaseDuration, CancellationToken.None).ConfigureAwait(false);
            firstClaimMilliseconds = Stopwatch.GetElapsedTime(claimStarted).TotalMilliseconds;
            Assert.IsNotNull(firstLease);
            Assert.AreEqual(1, firstLease.Record.AttemptCount);
            firstKey = firstLease.Record.IdempotencyKey;
            beforeReopen = await ReadDatabaseEvidenceAsync(observer, databasePath).ConfigureAwait(false);
            Assert.AreEqual(W3MetadataCount, beforeReopen.Records);
            Assert.AreEqual(W3MetadataCount, beforeReopen.UniqueIdempotencyKeys);
            Assert.AreEqual("wal", beforeReopen.JournalMode);
            Assert.AreEqual("ok", beforeReopen.IntegrityCheck);
        }

        var cpuAfterEnqueue = ReadCpuTime();
        var allocatedAfterEnqueue = GC.GetTotalAllocatedBytes(precise: true);
        var rssAfterEnqueue = Environment.WorkingSet;
        Assert.IsLessThanOrEqualTo(rssBefore + MaximumRssGrowthBytes, rssAfterEnqueue);

        clock.Advance(LeaseDuration + TimeSpan.FromSeconds(1));
        var reopenAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var reopenCpuBefore = ReadCpuTime();
        var reopenRssBefore = Environment.WorkingSet;
        var reopenStarted = Stopwatch.GetTimestamp();
        ArtifactOutboxSnapshot reopenedSnapshot;
        ArtifactOutboxLease reopenedLease;
        using (var reopened = new SqliteArtifactOutbox(clock))
        {
            await reopened.InitializeAsync(root, CancellationToken.None).ConfigureAwait(false);
            reopenedSnapshot = await reopened.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
            var claimed = await reopened.ClaimAsync(
                root, "issue-97-w3m-reopened", LeaseDuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(claimed);
            reopenedLease = claimed;
            Assert.AreEqual(firstKey, reopenedLease.Record.IdempotencyKey);
            Assert.AreEqual(2, reopenedLease.Record.AttemptCount);
            await reopened.RetryAsync(
                root, reopenedLease, clock.GetUtcNow() + RetryDelay, "performance-reopen", CancellationToken.None)
                .ConfigureAwait(false);
        }
        var reopenMilliseconds = Stopwatch.GetElapsedTime(reopenStarted).TotalMilliseconds;
        var afterReopen = ReadDatabaseFileEvidence(databasePath);
        Assert.AreEqual(W3MetadataCount, reopenedSnapshot.HeldCount);

        return new W3MetadataMeasurement(
            W3MetadataCount,
            enqueueMilliseconds,
            W3MetadataCount / (enqueueMilliseconds / 1000d),
            (cpuAfterEnqueue - cpuBefore).TotalMilliseconds,
            allocatedAfterEnqueue - allocatedBefore,
            rssBefore,
            rssAfterEnqueue,
            rssAfterEnqueue - rssBefore,
            firstClaimMilliseconds,
            queryPlan,
            beforeReopen,
            reopenMilliseconds,
            W3MetadataCount / (reopenMilliseconds / 1000d),
            (ReadCpuTime() - reopenCpuBefore).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - reopenAllocatedBefore,
            reopenRssBefore,
            Environment.WorkingSet,
            reopenedLease.Record.AttemptCount,
            reopenedSnapshot.HeldCount,
            afterReopen);
    }

    private static async Task<W3PayloadMeasurement> MeasureW3PayloadAsync(
        string root,
        string payloadRelativePath,
        string payloadSha256)
    {
        var clock = new MutableTimeProvider(StartUtc);
        var sourcePayloadPath = Path.Combine(root, payloadRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var manifests = Enumerable.Range(0, W3PayloadCount)
            .Select(index => CreateManifest(
                root,
                sourcePayloadPath,
                "issue-97-w3p",
                20_000 + index,
                $"payload/capture-{index:D5}.bin",
                payloadSha256,
                CanonicalPayloadBytes))
            .ToArray();
        using var handler = new CountingUploadHandler(clock, manifests);
        using var httpClient = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://issue-97.local/") };
        var uploadClient = new ArtifactUploadClient(
            new SingleHttpClientFactory(httpClient),
            Options.Create(new CameraAgentHostOptions { UploadBandwidthLimitBytesPerSecond = 0 }),
            clock);
        var rssSamples = new List<long>();
        StabilizeGc();
        var totalAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var totalCpuBefore = ReadCpuTime();
        var totalRssBefore = Environment.WorkingSet;
        double enqueueMilliseconds;
        ArtifactOutboxSnapshot initialBacklog;
        DrainCycleMeasurement outageOne;

        using (var outbox = new SqliteArtifactOutbox(clock))
        {
            var enqueueStarted = Stopwatch.GetTimestamp();
            foreach (var manifest in manifests)
            {
                await outbox.EnqueueAsync(root, manifest, CancellationToken.None).ConfigureAwait(false);
            }
            enqueueMilliseconds = Stopwatch.GetElapsedTime(enqueueStarted).TotalMilliseconds;
            initialBacklog = await outbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(W3PayloadCount, initialBacklog.HeldCount);
            Assert.AreEqual((long)W3PayloadCount * CanonicalPayloadBytes, initialBacklog.HeldBytes);
            handler.ReturnServiceUnavailable = true;
            outageOne = await DrainCycleAsync(
                outbox, root, uploadClient, handler, clock, ArtifactUploadDisposition.Retry, rssSamples).ConfigureAwait(false);
            Assert.AreEqual(W3PayloadCount, outageOne.Records);
        }

        DrainCycleMeasurement outageTwo;
        double firstReopenMilliseconds;
        using (var reopened = new SqliteArtifactOutbox(clock))
        {
            var reopenStarted = Stopwatch.GetTimestamp();
            await reopened.InitializeAsync(root, CancellationToken.None).ConfigureAwait(false);
            var persisted = await reopened.ReadAsync(
                root, manifests[0].IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
            firstReopenMilliseconds = Stopwatch.GetElapsedTime(reopenStarted).TotalMilliseconds;
            Assert.IsNotNull(persisted);
            Assert.AreEqual(1, persisted.AttemptCount);
            Assert.AreEqual(ArtifactOutboxStatus.Retry, persisted.Status);
            clock.Advance(RetryDelay);
            outageTwo = await DrainCycleAsync(
                reopened, root, uploadClient, handler, clock, ArtifactUploadDisposition.Retry, rssSamples).ConfigureAwait(false);
            Assert.AreEqual(W3PayloadCount, outageTwo.Records);
        }

        DrainCycleMeasurement recovery;
        double secondReopenMilliseconds;
        ArtifactOutboxSnapshot converged;
        QuarantineIsolationMeasurement quarantine;
        int minimumPersistedAttempts = int.MaxValue;
        int maximumPersistedAttempts = int.MinValue;
        using (var reopened = new SqliteArtifactOutbox(clock))
        {
            var reopenStarted = Stopwatch.GetTimestamp();
            await reopened.InitializeAsync(root, CancellationToken.None).ConfigureAwait(false);
            var persisted = await reopened.ReadAsync(
                root, manifests[0].IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
            secondReopenMilliseconds = Stopwatch.GetElapsedTime(reopenStarted).TotalMilliseconds;
            Assert.IsNotNull(persisted);
            Assert.AreEqual(2, persisted.AttemptCount);
            Assert.AreEqual(ArtifactOutboxStatus.Retry, persisted.Status);

            clock.Advance(RetryDelay);
            handler.ReturnServiceUnavailable = false;
            recovery = await DrainCycleAsync(
                reopened, root, uploadClient, handler, clock, ArtifactUploadDisposition.Acknowledged, rssSamples).ConfigureAwait(false);
            Assert.AreEqual(W3PayloadCount, recovery.Records);
            Assert.IsGreaterThan(10d, recovery.RecordsPerSecond);
            converged = await reopened.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0L, converged.HeldCount);
            Assert.AreEqual(W3PayloadCount, converged.AcknowledgedCount);
            Assert.AreEqual(0L, converged.PendingCount);
            Assert.AreEqual(0L, converged.LeasedCount);
            Assert.AreEqual(0L, converged.RetryCount);
            Assert.AreEqual(0L, converged.QuarantinedCount);

            foreach (var manifest in manifests)
            {
                var record = await reopened.ReadAsync(
                    root, manifest.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(record);
                Assert.AreEqual(ArtifactOutboxStatus.Acknowledged, record.Status);
                Assert.AreEqual(3, record.AttemptCount);
                Assert.IsTrue(record.Acknowledgement.HasValue);
                minimumPersistedAttempts = Math.Min(minimumPersistedAttempts, record.AttemptCount);
                maximumPersistedAttempts = Math.Max(maximumPersistedAttempts, record.AttemptCount);
            }

            quarantine = await RunQuarantineIsolationAsync(
                reopened, root, uploadClient, handler, payloadRelativePath, payloadSha256).ConfigureAwait(false);
        }

        Assert.AreEqual(payloadSha256, await ComputeSha256Async(Path.Combine(root, payloadRelativePath)).ConfigureAwait(false));
        Assert.AreEqual(CanonicalPayloadBytes, new FileInfo(Path.Combine(root, payloadRelativePath)).Length);
        Assert.AreEqual(W3PayloadCount * 3, handler.OutageAndRecoveryRequests);
        Assert.AreEqual(W3PayloadCount * 2, handler.ServiceUnavailableResponses);
        Assert.AreEqual(W3PayloadCount, handler.OutageAndRecoveryAcknowledgements);
        Assert.HasCount(30, rssSamples);
        var firstHalfRssMedian = Median(rssSamples.Take(rssSamples.Count / 2));
        var finalHalfRssMedian = Median(rssSamples.Skip(rssSamples.Count / 2));
        Assert.IsLessThanOrEqualTo(firstHalfRssMedian + MaximumRssGrowthBytes, finalHalfRssMedian);
        var database = ReadDatabaseFileEvidence(DatabasePath(root));

        return new W3PayloadMeasurement(
            W3PayloadCount,
            (long)W3PayloadCount * CanonicalPayloadBytes,
            enqueueMilliseconds,
            initialBacklog,
            outageOne,
            firstReopenMilliseconds,
            outageTwo,
            secondReopenMilliseconds,
            recovery,
            minimumPersistedAttempts,
            maximumPersistedAttempts,
            handler.OutageAndRecoveryRequests,
            handler.OutageAndRecoveryConsumedBytes,
            (long)handler.OutageAndRecoveryRequests * CanonicalPayloadBytes,
            converged,
            (ReadCpuTime() - totalCpuBefore).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - totalAllocatedBefore,
            totalRssBefore,
            Environment.WorkingSet,
            rssSamples,
            firstHalfRssMedian,
            finalHalfRssMedian,
            finalHalfRssMedian - firstHalfRssMedian,
            database,
            quarantine);
    }

    private static async Task<DrainCycleMeasurement> DrainCycleAsync(
        SqliteArtifactOutbox outbox,
        string root,
        ArtifactUploadClient uploadClient,
        CountingUploadHandler handler,
        MutableTimeProvider clock,
        ArtifactUploadDisposition expectedDisposition,
        List<long> rssSamples)
    {
        var requestsBefore = handler.TotalRequests;
        var bytesBefore = handler.TotalConsumedBytes;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = ReadCpuTime();
        var rssBefore = Environment.WorkingSet;
        var started = Stopwatch.GetTimestamp();
        var records = 0;
        while (await outbox.ClaimAsync(
            root, "issue-97-w3p-drain", LeaseDuration, CancellationToken.None).ConfigureAwait(false) is { } lease)
        {
            var result = await uploadClient.UploadAsync(root, lease.Record, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(expectedDisposition, result.Disposition);
            if (result.Disposition == ArtifactUploadDisposition.Retry)
            {
                await outbox.RetryAsync(
                    root, lease, clock.GetUtcNow() + RetryDelay, result.Reason, CancellationToken.None).ConfigureAwait(false);
            }
            else if (result.Disposition == ArtifactUploadDisposition.Acknowledged)
            {
                var acknowledgement = result.Acknowledgement
                    ?? throw new AssertFailedException("An acknowledged upload omitted acknowledgement evidence.");
                await outbox.AcknowledgeAsync(root, lease, acknowledgement, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await outbox.QuarantineAsync(root, lease, result.Reason, CancellationToken.None).ConfigureAwait(false);
            }
            records++;
            if (records % 10 == 0)
            {
                rssSamples.Add(Environment.WorkingSet);
            }
        }
        var duration = Stopwatch.GetElapsedTime(started);
        var snapshot = await outbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        return new DrainCycleMeasurement(
            records,
            duration.TotalMilliseconds,
            records / duration.TotalSeconds,
            handler.TotalRequests - requestsBefore,
            handler.TotalConsumedBytes - bytesBefore,
            (long)records * CanonicalPayloadBytes,
            (ReadCpuTime() - cpuBefore).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            rssBefore,
            Environment.WorkingSet,
            snapshot.PendingCount,
            snapshot.RetryCount,
            snapshot.AcknowledgedCount,
            snapshot.QuarantinedCount);
    }

    private static async Task<QuarantineIsolationMeasurement> RunQuarantineIsolationAsync(
        SqliteArtifactOutbox outbox,
        string root,
        ArtifactUploadClient uploadClient,
        CountingUploadHandler handler,
        string payloadRelativePath,
        string payloadSha256)
    {
        var conflict = CreateManifest(
            root, Path.Combine(root, payloadRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            "issue-97-quarantine", 40_000, "payload/quarantine-conflict.bin", payloadSha256, CanonicalPayloadBytes);
        var follower = CreateManifest(
            root, Path.Combine(root, payloadRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            "issue-97-quarantine", 40_001, "payload/quarantine-follower.bin", payloadSha256, CanonicalPayloadBytes);
        handler.Register(conflict);
        handler.Register(follower);
        handler.ConflictIdempotencyKey = conflict.IdempotencyKey;
        await outbox.EnqueueAsync(root, conflict, CancellationToken.None).ConfigureAwait(false);
        await outbox.EnqueueAsync(root, follower, CancellationToken.None).ConfigureAwait(false);
        var bytesBefore = handler.TotalConsumedBytes;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = ReadCpuTime();
        var rssBefore = Environment.WorkingSet;
        var started = Stopwatch.GetTimestamp();

        var conflictLease = await outbox.ClaimAsync(
            root, "issue-97-quarantine", LeaseDuration, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(conflictLease);
        Assert.AreEqual(conflict.IdempotencyKey, conflictLease.Record.IdempotencyKey);
        var conflictResult = await uploadClient.UploadAsync(
            root, conflictLease.Record, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ArtifactUploadDisposition.Quarantine, conflictResult.Disposition);
        Assert.AreEqual("http-409", conflictResult.Reason);
        await outbox.QuarantineAsync(
            root, conflictLease, conflictResult.Reason, CancellationToken.None).ConfigureAwait(false);

        var followerLease = await outbox.ClaimAsync(
            root, "issue-97-quarantine", LeaseDuration, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(followerLease);
        Assert.AreEqual(follower.IdempotencyKey, followerLease.Record.IdempotencyKey);
        var followerResult = await uploadClient.UploadAsync(
            root, followerLease.Record, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ArtifactUploadDisposition.Acknowledged, followerResult.Disposition);
        var acknowledgement = followerResult.Acknowledgement
            ?? throw new AssertFailedException("The isolation follower omitted acknowledgement evidence.");
        await outbox.AcknowledgeAsync(
            root, followerLease, acknowledgement, CancellationToken.None).ConfigureAwait(false);

        var isolated = await outbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, isolated.QuarantinedCount);
        Assert.AreEqual(1L, isolated.HeldCount);
        Assert.AreEqual(W3PayloadCount + 1L, isolated.AcknowledgedCount);
        await outbox.AbandonAsync(
            root, conflict.IdempotencyKey, "issue-97-performance", "confirmed-http-409", CancellationToken.None)
            .ConfigureAwait(false);
        var converged = await outbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, converged.HeldCount);
        Assert.AreEqual(1L, converged.AbandonedCount);
        var audit = await outbox.ReadAuditAsync(
            root, conflict.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(QuarantineActions, audit.Select(static entry => entry.Action).ToArray());
        handler.ConflictIdempotencyKey = null;

        return new QuarantineIsolationMeasurement(
            409,
            conflictResult.Reason,
            followerResult.Disposition.ToString(),
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            handler.TotalConsumedBytes - bytesBefore,
            (ReadCpuTime() - cpuBefore).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            rssBefore,
            Environment.WorkingSet,
            isolated.HeldCount,
            converged.HeldCount,
            audit.Select(static entry => entry.Action).ToArray());
    }

    private static ArtifactManifestV2 CreateManifest(
        string root,
        string sourcePayloadPath,
        string agentId,
        int index,
        string relativePath,
        string payloadSha256,
        long payloadLength)
    {
        var payloadPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        if (!File.Exists(payloadPath))
        {
            if (CreateHardLink(sourcePayloadPath, payloadPath) != 0)
            {
                throw new IOException($"Unable to create performance fixture hard link (errno {Marshal.GetLastPInvokeError()}).");
            }
        }
        var createdUtc = StartUtc.AddMilliseconds(index);
        var profile = new ProfileIdentityDescriptor("performance", "1.0.0", new string('A', 64));
        var descriptor = new ReconstructionDescriptor(
            new CaptureIdentityDescriptor(agentId, "performance-rig", index + 1L, CreateGuid(index, 0x2270)),
            new CaptureTimingDescriptor(
                createdUtc.AddSeconds(-4), createdUtc.AddSeconds(-3), createdUtc.AddSeconds(-2),
                createdUtc.AddSeconds(-1), createdUtc),
            new CaptureControlDescriptor(
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1, null, null, null, null),
            new CaptureProfileSet(profile, profile, profile, profile, profile),
            new FrameLayoutDescriptor(
                checked((int)payloadLength), 1, checked((int)payloadLength), CameraPixelFormat.Mono8,
                FrameByteOrder.NotApplicable, 8, 8, FrameSamplePacking.ByteAligned,
                ColorFilterArrayPattern.None, 0, 255, payloadLength),
            new ArtifactDescriptor(
                CreateGuid(index, 0x1170), FrameArtifactRole.Raw, "performance", "native", createdUtc, [],
                RecipeIdentityDescriptor.Create(
                    "capture-raw", "1.0.0", "performance", JsonSerializer.SerializeToElement(new { index })),
                "application/octet-stream", payloadSha256));
        var manifest = new ArtifactManifestV2(
            ArtifactManifestV2.CurrentSchemaVersion, descriptor, relativePath);
        File.WriteAllBytes(Path.ChangeExtension(payloadPath, ".json"), CaptureContractJson.Serialize(manifest));
        return manifest;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, BestFitMapping = false,
        ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int CreateHardLink(string existingPath, string newPath);

    private static Guid CreateGuid(int index, short marker)
        => new(index + 1, marker, marker, 1, 2, 3, 4, 5, 6, 7, 8);

    private static async Task<string> WriteDeterministicPayloadAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[64 * 1024];
            long offset = 0;
            while (offset < CanonicalPayloadBytes)
            {
                var count = (int)Math.Min(buffer.Length, CanonicalPayloadBytes - offset);
                for (var index = 0; index < count; index++)
                {
                    buffer[index] = (byte)(((offset + index) * 31 + 17) % 251);
                }
                await stream.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                hash.AppendData(buffer, 0, count);
                offset += count;
            }
            await stream.FlushAsync().ConfigureAwait(false);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
        }
    }

    private static async Task<IReadOnlyList<string>> ReadClaimQueryPlanAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT record_id FROM (
                SELECT * FROM (
                    SELECT record_id, next_attempt_unix_ms, created_unix_ms, idempotency_key
                    FROM artifact_outbox_records
                    WHERE status = 'pending'
                    ORDER BY next_attempt_unix_ms, created_unix_ms, idempotency_key
                    LIMIT 1)
                UNION ALL
                SELECT * FROM (
                    SELECT record_id, next_attempt_unix_ms, created_unix_ms, idempotency_key
                    FROM artifact_outbox_records
                    WHERE status = 'retry' AND next_attempt_unix_ms <= $now
                    ORDER BY next_attempt_unix_ms, created_unix_ms, idempotency_key
                    LIMIT 1)
                UNION ALL
                SELECT * FROM (
                    SELECT record_id, next_attempt_unix_ms, created_unix_ms, idempotency_key
                    FROM artifact_outbox_records
                    WHERE status = 'leased' AND lease_expires_unix_ms <= $now
                    ORDER BY lease_expires_unix_ms, created_unix_ms, idempotency_key
                    LIMIT 1)
            )
            ORDER BY next_attempt_unix_ms, created_unix_ms, idempotency_key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$now", StartUtc.ToUnixTimeMilliseconds());
        var details = new List<string>();
        using var reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
        while (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            details.Add(reader.GetString(3));
        }
        return details;
    }

    private static async Task<DatabaseEvidence> ReadDatabaseEvidenceAsync(
        SqliteConnection connection,
        string databasePath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM artifact_outbox_records),
                (SELECT COUNT(DISTINCT idempotency_key) FROM artifact_outbox_records),
                (SELECT journal_mode FROM pragma_journal_mode),
                (SELECT integrity_check FROM pragma_integrity_check);
            """;
        using var reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false));
        var files = ReadDatabaseFileEvidence(databasePath);
        return new DatabaseEvidence(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            files.DatabaseBytes,
            files.WalBytes);
    }

    private static DatabaseFileEvidence ReadDatabaseFileEvidence(string databasePath)
    {
        var walPath = string.Concat(databasePath, "-wal");
        return new DatabaseFileEvidence(
            new FileInfo(databasePath).Length,
            File.Exists(walPath) ? new FileInfo(walPath).Length : 0);
    }

    private static SqliteConnection OpenDatabase(string databasePath)
        => new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());

    private static string DatabasePath(string root)
        => Path.Combine(root, "outbox", "artifact-outbox.db");

    private static TimeSpan ReadCpuTime()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static void StabilizeGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task<string> ReadSqliteVersionAsync()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return Convert.ToString(
            await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static string ReadPinnedSdkVersion(string repositoryRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repositoryRoot, "global.json")));
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidDataException("global.json does not contain an SDK version.");
    }

    private static string ReadEvidenceRevision()
    {
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "working-tree";
        if (string.IsNullOrWhiteSpace(revision)
            || revision is "." or ".."
            || revision.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new InvalidOperationException("HVO_EVIDENCE_REVISION must be a single safe path segment.");
        }
        return revision;
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CountingUploadHandler : HttpMessageHandler
    {
        private readonly MutableTimeProvider _clock;
        private readonly Dictionary<string, ArtifactManifestV2> _manifests;

        public CountingUploadHandler(MutableTimeProvider clock, IEnumerable<ArtifactManifestV2> manifests)
        {
            _clock = clock;
            _manifests = manifests.ToDictionary(static manifest => manifest.IdempotencyKey, StringComparer.OrdinalIgnoreCase);
        }

        public bool ReturnServiceUnavailable { get; set; }

        public string? ConflictIdempotencyKey { get; set; }

        public int TotalRequests { get; private set; }

        public long TotalConsumedBytes { get; private set; }

        public int ServiceUnavailableResponses { get; private set; }

        public int OutageAndRecoveryRequests { get; private set; }

        public long OutageAndRecoveryConsumedBytes { get; private set; }

        public int OutageAndRecoveryAcknowledgements { get; private set; }

        public void Register(ArtifactManifestV2 manifest) => _manifests.Add(manifest.IdempotencyKey, manifest);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is null
                || !request.Headers.TryGetValues("Idempotency-Key", out var values)
                || values.SingleOrDefault() is not { } idempotencyKey
                || !_manifests.TryGetValue(idempotencyKey, out var manifest))
            {
                throw new InvalidDataException("The upload request did not identify a registered manifest.");
            }

            using var sink = new CountingWriteStream();
            await request.Content.CopyToAsync(sink, cancellationToken).ConfigureAwait(false);
            TotalRequests++;
            TotalConsumedBytes += sink.BytesWritten;
            var isW3PayloadRequest = string.Equals(
                manifest.Descriptor.Capture.AgentId, "issue-97-w3p", StringComparison.Ordinal);
            if (isW3PayloadRequest)
            {
                OutageAndRecoveryRequests++;
                OutageAndRecoveryConsumedBytes += sink.BytesWritten;
            }

            if (ReturnServiceUnavailable)
            {
                ServiceUnavailableResponses++;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            if (string.Equals(idempotencyKey, ConflictIdempotencyKey, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            }

            var acknowledgement = new ArtifactUploadAcknowledgement(
                ArtifactUploadAcknowledgement.CurrentSchemaVersion,
                manifest.IdempotencyKey,
                manifest.Descriptor.Artifact.ArtifactId,
                manifest.Descriptor.Artifact.ChecksumSha256,
                manifest.Descriptor.Layout.ByteLength,
                _clock.GetUtcNow(),
                manifest.SchemaVersion);
            acknowledgement.Validate();
            if (isW3PayloadRequest)
            {
                OutageAndRecoveryAcknowledgements++;
            }
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(acknowledgement)
            };
        }
    }

    private sealed class CountingWriteStream : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;

        public override void Write(ReadOnlySpan<byte> buffer) => BytesWritten += buffer.Length;

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BytesWritten += count;
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BytesWritten += buffer.Length;
            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed record DatabaseFileEvidence(long DatabaseBytes, long WalBytes);

    private sealed record DatabaseEvidence(
        long Records,
        long UniqueIdempotencyKeys,
        string JournalMode,
        string IntegrityCheck,
        long DatabaseBytes,
        long WalBytes);

    private sealed record W3MetadataMeasurement(
        int Records,
        double EnqueueMilliseconds,
        double EnqueueRecordsPerSecond,
        double EnqueueCpuMilliseconds,
        long EnqueueAllocatedBytes,
        long EnqueueRssBeforeBytes,
        long EnqueueRssAfterBytes,
        long EnqueueRssGrowthBytes,
        double FirstIndexedClaimMilliseconds,
        IReadOnlyList<string> ClaimQueryPlan,
        DatabaseEvidence BeforeReopen,
        double ReopenAndClaimMilliseconds,
        double ReopenDiscoveryRecordsPerSecond,
        double ReopenCpuMilliseconds,
        long ReopenAllocatedBytes,
        long ReopenRssBeforeBytes,
        long ReopenRssAfterBytes,
        int PersistedAttemptCount,
        long ReopenedHeldRecords,
        DatabaseFileEvidence AfterReopen);

    private sealed record DrainCycleMeasurement(
        int Records,
        double DurationMilliseconds,
        double RecordsPerSecond,
        int HttpRequests,
        long ConsumedRequestBytes,
        long PayloadBytes,
        double CpuMilliseconds,
        long AllocatedBytes,
        long RssBeforeBytes,
        long RssAfterBytes,
        long PendingBacklog,
        long RetryBacklog,
        long AcknowledgedRecords,
        long QuarantinedRecords);

    private sealed record QuarantineIsolationMeasurement(
        int StatusCode,
        string QuarantineReason,
        string FollowerDisposition,
        double DurationMilliseconds,
        long ConsumedRequestBytes,
        double CpuMilliseconds,
        long AllocatedBytes,
        long RssBeforeBytes,
        long RssAfterBytes,
        long HeldBeforeOperatorResolution,
        long HeldAfterOperatorResolution,
        IReadOnlyList<string> AuditActions);

    private sealed record W3PayloadMeasurement(
        int Records,
        long LogicalPayloadBytes,
        double EnqueueMilliseconds,
        ArtifactOutboxSnapshot InitialBacklog,
        DrainCycleMeasurement OutageOne,
        double FirstReopenMilliseconds,
        DrainCycleMeasurement OutageTwo,
        double SecondReopenMilliseconds,
        DrainCycleMeasurement Recovery,
        int MinimumPersistedAttempts,
        int MaximumPersistedAttempts,
        int HttpAttempts,
        long ConsumedRequestBytes,
        long AttemptedPayloadBytes,
        ArtifactOutboxSnapshot ConvergedBacklog,
        double TotalCpuMilliseconds,
        long TotalAllocatedBytes,
        long TotalRssBeforeBytes,
        long TotalRssAfterBytes,
        IReadOnlyList<long> RssSamplesBytes,
        long FirstHalfRssMedianBytes,
        long FinalHalfRssMedianBytes,
        long RssMedianGrowthBytes,
        DatabaseFileEvidence Database,
        QuarantineIsolationMeasurement QuarantineIsolation);

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif
}
