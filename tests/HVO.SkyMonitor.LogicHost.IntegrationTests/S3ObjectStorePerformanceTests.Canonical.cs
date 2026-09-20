using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.DependencyInjection;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class S3ObjectStorePerformanceTests
{
    private const int W1Bytes = 4_708_352;
    private const int W2Bytes = 12_879_360;
    private static readonly string[] AllowedObjectStoreActivityTags =
        ["object_store.operation", "object_store.outcome", "object_store.bucket_role", "object_store.addressing_style"];

    [TestMethod]
    [Timeout(1_800_000)]
    public async Task CanonicalMatrix_RecordsComparativeS3BoundaryEvidence()
    {
        var runId = Guid.NewGuid().ToString("N");
        var root = $"issue-504/performance/{runId}/matrix/";
        var fixture = AssemblyHooks.Fixture;
        var fixtureClient = fixture.Factory.Services.GetRequiredService<IMinioClient>();
        if (!await fixtureClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket)).ConfigureAwait(false))
        {
            await fixtureClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket)).ConfigureAwait(false);
        }

        using var sockets = new SocketsHttpHandler();
        using var handler = new CountingHandler { InnerHandler = sockets };
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        using var directClient = new MinioClient();
        _ = directClient
            .WithEndpoint(IntegrationTestFixture.ExternalS3Endpoint)
            .WithCredentials(IntegrationTestFixture.ExternalS3AccessKey, IntegrationTestFixture.ExternalS3SecretKey)
            .WithHttpClient(httpClient, disposeHttpClient: false)
            .Build();
        var options = CreateS3Options();
        using var candidateClient = S3ObjectStoreClientFactory.Create(
            options,
            new ObjectStoreTestClient.SharedHttpClientFactory(httpClient));
        var store = ObjectStoreTestClient.Create(candidateClient, options);
        using var signals = new ObjectStoreSignalCollector();

        try
        {
            var w1Baseline = await MeasureWorkflowsAsync(
                "W1-baseline",
                handler,
                warmups: 5,
                operations: 30,
                concurrency: 1,
                (index, cancellationToken) => ExecuteDirectWorkflowAsync(
                    directClient, $"{root}w1/baseline/{index:D4}", W1Bytes, cancellationToken)).ConfigureAwait(false);
            var w1Candidate = await MeasureWorkflowsAsync(
                "W1-candidate",
                handler,
                warmups: 5,
                operations: 30,
                concurrency: 1,
                (index, cancellationToken) => ExecuteCandidateWorkflowAsync(
                    store, $"{root}w1/candidate/{index:D4}", W1Bytes, cancellationToken)).ConfigureAwait(false);
            AssertNoMaterialRegression(w1Baseline, w1Candidate);

            var w2Baseline = await MeasureWorkflowsAsync(
                "W2-baseline",
                handler,
                warmups: 5,
                operations: 30,
                concurrency: 1,
                (index, cancellationToken) => ExecuteDirectWorkflowAsync(
                    directClient, $"{root}w2/baseline/{index:D4}", W2Bytes, cancellationToken)).ConfigureAwait(false);
            var w2Candidate = await MeasureWorkflowsAsync(
                "W2-candidate",
                handler,
                warmups: 5,
                operations: 30,
                concurrency: 1,
                (index, cancellationToken) => ExecuteCandidateWorkflowAsync(
                    store, $"{root}w2/candidate/{index:D4}", W2Bytes, cancellationToken)).ConfigureAwait(false);
            AssertNoMaterialRegression(w2Baseline, w2Candidate);
            var timeToFirstByte = await MeasureTimeToFirstByteAsync(
                directClient,
                store,
                root + "ttfb/").ConfigureAwait(false);

            var w3mPrefix = root + "w3m/";
            await SeedObjectsAsync(directClient, w3mPrefix, 10_000, payloadBytes: 1, concurrency: 32)
                .ConfigureAwait(false);
            var w3mBaseline = await MeasureDirectListAsync(directClient, handler, w3mPrefix, 10_000)
                .ConfigureAwait(false);
            var w3mCandidate = await MeasureCandidateListAsync(store, handler, w3mPrefix, 10_000)
                .ConfigureAwait(false);
            Assert.IsTrue(w3mCandidate.ElapsedMilliseconds <= w3mBaseline.ElapsedMilliseconds * 2 + 500);
            Assert.IsTrue(w3mCandidate.Protocol.Requests <= w3mBaseline.Protocol.Requests + 1);

            var w3pBaselinePrefix = root + "w3p/baseline/";
            var w3pCandidatePrefix = root + "w3p/candidate/";
            await SeedObjectsAsync(directClient, w3pBaselinePrefix, 100, W2Bytes, concurrency: 8).ConfigureAwait(false);
            await SeedObjectsAsync(directClient, w3pCandidatePrefix, 100, W2Bytes, concurrency: 8).ConfigureAwait(false);
            var w3pBaseline = await MeasureDirectDrainAsync(directClient, handler, w3pBaselinePrefix, 100, W2Bytes)
                .ConfigureAwait(false);
            var restartMilliseconds = await RestartProviderAsync(store).ConfigureAwait(false);
            var w3pCandidate = await MeasureCandidateDrainAsync(store, handler, w3pCandidatePrefix, 100, W2Bytes)
                .ConfigureAwait(false);
            Assert.IsTrue(w3pCandidate.DrainMilliseconds <= w3pBaseline.DrainMilliseconds * 2 + 2000);

            var w4 = new List<ConcurrencyComparison>();
            foreach (var concurrency in new[] { 1, 4, 8 })
            {
                var baseline = await MeasureWorkflowsAsync(
                    $"W4-c{concurrency}-baseline",
                    handler,
                    warmups: 20,
                    operations: 200,
                    concurrency,
                    (index, cancellationToken) => ExecuteDirectWorkflowAsync(
                        directClient, $"{root}w4/c{concurrency}/baseline/{index:D4}", W1Bytes, cancellationToken))
                    .ConfigureAwait(false);
                var candidate = await MeasureWorkflowsAsync(
                    $"W4-c{concurrency}-candidate",
                    handler,
                    warmups: 20,
                    operations: 200,
                    concurrency,
                    (index, cancellationToken) => ExecuteCandidateWorkflowAsync(
                        store, $"{root}w4/c{concurrency}/candidate/{index:D4}", W1Bytes, cancellationToken))
                    .ConfigureAwait(false);
                AssertNoMaterialRegression(baseline, candidate);
                w4.Add(new ConcurrencyComparison(concurrency, baseline, candidate));
            }

            var restore = await MeasureRestoreAsync(store, root + "restore/").ConfigureAwait(false);
            Assert.IsTrue(restore.ElapsedMilliseconds > 0);
            Assert.AreEqual(W2Bytes, restore.ContentLength);
            Assert.AreEqual(ContentType, restore.ContentType);

            Assert.IsTrue(await store.BucketExistsAsync(Bucket, CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(signals.PeakActiveStreams is >= 1 and <= 8);
            Assert.IsTrue(signals.ActivityCount > 0);
            CollectionAssert.IsSubsetOf(
                signals.ActivityTagKeys.ToArray(),
                AllowedObjectStoreActivityTags);

            var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "local-uncommitted";
            var evidence = new
            {
                Schema = "hvo-issue-504-object-store-canonical-performance-v1",
                Revision = revision,
                RunId = runId,
                Topology = "In-process test and LogicHost adapter with the same isolated MinIO S3 Testcontainer for baseline and candidate",
                Workloads = new
                {
                    W1 = new { PayloadBytes = W1Bytes, Warmups = 5, Operations = 30, Baseline = w1Baseline, Candidate = w1Candidate },
                    W2 = new { PayloadBytes = W2Bytes, Warmups = 5, Operations = 30, Baseline = w2Baseline, Candidate = w2Candidate },
                    TimeToFirstByte = timeToFirstByte,
                    W3M = new { Records = 10_000, Baseline = w3mBaseline, Candidate = w3mCandidate },
                    W3P = new { Payloads = 100, PayloadBytes = W2Bytes, Baseline = w3pBaseline, Candidate = w3pCandidate },
                    W4 = w4
                },
                ProviderRestartMilliseconds = restartMilliseconds,
                Restore = restore,
                Signals = new
                {
                    signals.PeakActiveStreams,
                    signals.ActivityCount,
                    MetricNames = signals.MetricNames.Order(StringComparer.Ordinal),
                    ActivityTagKeys = signals.ActivityTagKeys.Order(StringComparer.Ordinal)
                },
                Health = "authenticated-bucket-check-passed",
                Integrity = "exact-length-content-type-sha256-generation-and-strong-visibility-pass",
                LeakageScan = "bounded-metric-and-span-dimensions-pass",
                Bounds = "candidate p95 <= baseline*2+50ms; allocations <= baseline*2+64MiB; RSS growth <=256MiB; list/drain <= baseline*2+bounded allowance"
            };
            var outputDirectory = Path.Combine(
                FindRepositoryRoot(),
                "TestResults",
                "issue-504",
                revision,
                runId);
            Directory.CreateDirectory(outputDirectory);
            var evidencePath = Path.Combine(outputDirectory, "s3-object-store-canonical-performance.json");
            await File.WriteAllTextAsync(
                evidencePath,
                JsonSerializer.Serialize(evidence, EvidenceJsonOptions)).ConfigureAwait(false);
            TestContext.AddResultFile(evidencePath);
            TestContext.WriteLine("Issue #504 canonical S3 performance evidence: {0}", evidencePath);
        }
        finally
        {
            await DeletePrefixAsync(directClient, root).ConfigureAwait(false);
        }
    }

    private static async Task<WorkflowMeasurement> MeasureWorkflowsAsync(
        string name,
        CountingHandler handler,
        int warmups,
        int operations,
        int concurrency,
        Func<int, CancellationToken, Task> workflow)
    {
        await Parallel.ForEachAsync(
            Enumerable.Range(-warmups, warmups),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (index, cancellationToken) => await workflow(index, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var rssBefore = process.WorkingSet64;
        var providerBefore = await ReadProviderResourcesAsync().ConfigureAwait(false);
        handler.Reset();
        var latencies = new double[operations];
        var started = Stopwatch.GetTimestamp();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, operations),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (index, cancellationToken) =>
            {
                var operationStarted = Stopwatch.GetTimestamp();
                await workflow(index, cancellationToken).ConfigureAwait(false);
                latencies[index] = Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds;
            }).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var providerAfter = await ReadProviderResourcesAsync().ConfigureAwait(false);
        Array.Sort(latencies);
        return new WorkflowMeasurement(
            name,
            operations,
            concurrency,
            elapsed.TotalMilliseconds,
            latencies[operations / 2],
            latencies[(int)Math.Ceiling(operations * 0.95) - 1],
            operations / elapsed.TotalSeconds,
            process.TotalProcessorTime.TotalMilliseconds - cpuBefore.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore,
            rssBefore,
            process.WorkingSet64,
            handler.Snapshot(),
            ProviderResourceDelta.Create(providerBefore, providerAfter));
    }

    private static async Task ExecuteCandidateWorkflowAsync(
        IObjectStore store,
        string key,
        int payloadBytes,
        CancellationToken cancellationToken)
    {
        var stagingKey = key + ".staging";
        var canonicalKey = key + ".canonical";
        using var source = new PatternReadStream(payloadBytes);
        await store.PutAsync(Bucket, stagingKey, source, payloadBytes, ContentType, cancellationToken).ConfigureAwait(false);
        var expected = source.GetSha256();
        var staged = await store.StatAsync(Bucket, stagingKey, cancellationToken).ConfigureAwait(false);
        CollectionAssert.AreEqual(expected, await ReadCandidateSha256Async(store, stagingKey, staged.Generation, cancellationToken).ConfigureAwait(false));
        await store.CopyAsync(Bucket, stagingKey, canonicalKey, cancellationToken).ConfigureAwait(false);
        var canonical = await store.StatAsync(Bucket, canonicalKey, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(payloadBytes, canonical.ContentLength);
        Assert.AreEqual(ContentType, canonical.ContentType);
        CollectionAssert.AreEqual(expected, await ReadCandidateSha256Async(store, canonicalKey, canonical.Generation, cancellationToken).ConfigureAwait(false));
        await store.DeleteAsync(Bucket, stagingKey, cancellationToken).ConfigureAwait(false);
        await store.DeleteAsync(Bucket, canonicalKey, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteDirectWorkflowAsync(
        MinioClient client,
        string key,
        int payloadBytes,
        CancellationToken cancellationToken)
    {
        var stagingKey = key + ".staging";
        var canonicalKey = key + ".canonical";
        using var source = new PatternReadStream(payloadBytes);
        await client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(Bucket).WithObject(stagingKey).WithContentType(ContentType)
            .WithStreamData(source).WithObjectSize(payloadBytes), cancellationToken).ConfigureAwait(false);
        var expected = source.GetSha256();
        var staged = await client.StatObjectAsync(
            new StatObjectArgs().WithBucket(Bucket).WithObject(stagingKey), cancellationToken).ConfigureAwait(false);
        CollectionAssert.AreEqual(expected, await ReadDirectSha256Async(client, stagingKey, staged.ETag, cancellationToken).ConfigureAwait(false));
        await client.CopyObjectAsync(new CopyObjectArgs()
            .WithBucket(Bucket).WithObject(canonicalKey)
            .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(Bucket).WithObject(stagingKey)), cancellationToken)
            .ConfigureAwait(false);
        var canonical = await client.StatObjectAsync(
            new StatObjectArgs().WithBucket(Bucket).WithObject(canonicalKey), cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(payloadBytes, canonical.Size);
        Assert.AreEqual(ContentType, canonical.ContentType);
        CollectionAssert.AreEqual(expected, await ReadDirectSha256Async(client, canonicalKey, canonical.ETag, cancellationToken).ConfigureAwait(false));
        await client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(stagingKey), cancellationToken).ConfigureAwait(false);
        await client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(canonicalKey), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadCandidateSha256Async(
        IObjectStore store,
        string key,
        string generation,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await store.ReadAsync(Bucket, key, generation, async (content, token) =>
        {
            var buffer = GC.AllocateUninitializedArray<byte>(128 * 1024);
            int read;
            while ((read = await content.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }
        }, cancellationToken).ConfigureAwait(false);
        return hash.GetHashAndReset();
    }

    private static async Task<byte[]> ReadDirectSha256Async(
        MinioClient client,
        string key,
        string generation,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await client.GetObjectAsync(new GetObjectArgs()
            .WithBucket(Bucket)
            .WithObject(key)
            .WithHeaders(new Dictionary<string, string>(StringComparer.Ordinal) { ["If-Match"] = $"\"{generation}\"" })
            .WithCallbackStream(async (content, token) =>
            {
                var buffer = GC.AllocateUninitializedArray<byte>(128 * 1024);
                int read;
                while ((read = await content.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer.AsSpan(0, read));
                }
            }), cancellationToken).ConfigureAwait(false);
        return hash.GetHashAndReset();
    }

    private static async Task SeedObjectsAsync(
        MinioClient client,
        string prefix,
        int count,
        int payloadBytes,
        int concurrency)
        => await Parallel.ForEachAsync(
            Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (index, cancellationToken) =>
            {
                using var source = new PatternReadStream(payloadBytes);
                await client.PutObjectAsync(new PutObjectArgs()
                    .WithBucket(Bucket)
                    .WithObject($"{prefix}{index:D5}.bin")
                    .WithContentType(ContentType)
                    .WithStreamData(source)
                    .WithObjectSize(payloadBytes), cancellationToken).ConfigureAwait(false);
                Assert.AreEqual(payloadBytes, source.BytesRead);
            }).ConfigureAwait(false);

    private static async Task<ListMeasurement> MeasureDirectListAsync(
        MinioClient client,
        CountingHandler handler,
        string prefix,
        int expectedCount)
    {
        handler.Reset();
        var providerBefore = await ReadProviderResourcesAsync().ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        var items = new List<(string Key, long Length)>();
        await foreach (var item in client.ListObjectsEnumAsync(
            new ListObjectsArgs().WithBucket(Bucket).WithPrefix(prefix).WithRecursive(true),
            CancellationToken.None))
        {
            items.Add((item.Key, checked((long)item.Size)));
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        var providerAfter = await ReadProviderResourcesAsync().ConfigureAwait(false);
        AssertList(items, expectedCount);
        return new ListMeasurement(
            elapsed.TotalMilliseconds,
            items.Count,
            items.Sum(item => item.Length),
            handler.Snapshot(),
            ProviderResourceDelta.Create(providerBefore, providerAfter));
    }

    private static async Task<ListMeasurement> MeasureCandidateListAsync(
        IObjectStore store,
        CountingHandler handler,
        string prefix,
        int expectedCount)
    {
        handler.Reset();
        var providerBefore = await ReadProviderResourcesAsync().ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        var items = new List<(string Key, long Length)>();
        await foreach (var item in store.ListAsync(Bucket, prefix, CancellationToken.None))
        {
            Assert.AreEqual(TimeSpan.Zero, item.LastModifiedUtc.Offset);
            items.Add((item.Key, item.ContentLength));
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        var providerAfter = await ReadProviderResourcesAsync().ConfigureAwait(false);
        AssertList(items, expectedCount);
        return new ListMeasurement(
            elapsed.TotalMilliseconds,
            items.Count,
            items.Sum(item => item.Length),
            handler.Snapshot(),
            ProviderResourceDelta.Create(providerBefore, providerAfter));
    }

    private static void AssertList(List<(string Key, long Length)> items, int expectedCount)
    {
        Assert.AreEqual(expectedCount, items.Count);
        CollectionAssert.AreEqual(
            items.Select(item => item.Key).Order(StringComparer.Ordinal).ToArray(),
            items.Select(item => item.Key).ToArray());
        Assert.HasCount(expectedCount, items.Select(item => item.Key).Distinct(StringComparer.Ordinal));
    }

    private static async Task<BacklogMeasurement> MeasureDirectDrainAsync(
        MinioClient client,
        CountingHandler handler,
        string prefix,
        int expectedCount,
        int payloadBytes)
    {
        var keys = new List<string>();
        var oldest = DateTimeOffset.MaxValue;
        await foreach (var item in client.ListObjectsEnumAsync(
            new ListObjectsArgs().WithBucket(Bucket).WithPrefix(prefix).WithRecursive(true),
            CancellationToken.None))
        {
            keys.Add(item.Key);
            if (item.LastModifiedDateTime is { } modified)
            {
                var modifiedUtc = new DateTimeOffset(modified.ToUniversalTime());
                if (modifiedUtc < oldest) oldest = modifiedUtc;
            }
        }
        Assert.HasCount(expectedCount, keys);
        handler.Reset();
        var providerBefore = await ReadProviderResourcesAsync().ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        await Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (key, token) => await client.RemoveObjectAsync(
                new RemoveObjectArgs().WithBucket(Bucket).WithObject(key), token).ConfigureAwait(false)).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var providerAfter = await ReadProviderResourcesAsync().ConfigureAwait(false);
        return new BacklogMeasurement(
            expectedCount,
            (long)expectedCount * payloadBytes,
            oldest == DateTimeOffset.MaxValue ? 0 : Math.Max(0, (DateTimeOffset.UtcNow - oldest).TotalMilliseconds),
            elapsed.TotalMilliseconds,
            expectedCount / elapsed.TotalSeconds,
            0,
            handler.Snapshot(),
            ProviderResourceDelta.Create(providerBefore, providerAfter));
    }

    private static async Task<BacklogMeasurement> MeasureCandidateDrainAsync(
        IObjectStore store,
        CountingHandler handler,
        string prefix,
        int expectedCount,
        int payloadBytes)
    {
        var items = new List<ObjectStoreItem>();
        await foreach (var item in store.ListAsync(Bucket, prefix, CancellationToken.None))
        {
            items.Add(item);
        }
        Assert.HasCount(expectedCount, items);
        Assert.AreEqual((long)expectedCount * payloadBytes, items.Sum(item => item.ContentLength));
        handler.Reset();
        var providerBefore = await ReadProviderResourcesAsync().ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (item, token) => await store.DeleteAsync(Bucket, item.Key, token).ConfigureAwait(false)).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var providerAfter = await ReadProviderResourcesAsync().ConfigureAwait(false);
        var remaining = 0;
        await foreach (var _ in store.ListAsync(Bucket, prefix, CancellationToken.None)) remaining++;
        return new BacklogMeasurement(
            items.Count,
            items.Sum(item => item.ContentLength),
            Math.Max(0, (DateTimeOffset.UtcNow - items.Min(item => item.LastModifiedUtc)).TotalMilliseconds),
            elapsed.TotalMilliseconds,
            items.Count / elapsed.TotalSeconds,
            remaining,
            handler.Snapshot(),
            ProviderResourceDelta.Create(providerBefore, providerAfter));
    }

    private static async Task<TimeToFirstByteComparison> MeasureTimeToFirstByteAsync(
        MinioClient client,
        IObjectStore store,
        string prefix)
    {
        var key = prefix + "source.bin";
        using (var source = new PatternReadStream(W1Bytes))
        {
            await client.PutObjectAsync(new PutObjectArgs()
                .WithBucket(Bucket).WithObject(key).WithContentType(ContentType)
                .WithStreamData(source).WithObjectSize(W1Bytes)).ConfigureAwait(false);
        }
        var stat = await store.StatAsync(Bucket, key, CancellationToken.None).ConfigureAwait(false);
        var baseline = new double[30];
        var candidate = new double[30];
        for (var index = -5; index < 30; index++)
        {
            var started = Stopwatch.GetTimestamp();
            double firstByte = 0;
            await client.GetObjectAsync(new GetObjectArgs()
                .WithBucket(Bucket)
                .WithObject(key)
                .WithHeaders(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["If-Match"] = $"\"{stat.Generation}\""
                })
                .WithCallbackStream(async (content, token) =>
                {
                    var buffer = new byte[1];
                    Assert.AreEqual(1, await content.ReadAsync(buffer, token).ConfigureAwait(false));
                    firstByte = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    await content.CopyToAsync(Stream.Null, token).ConfigureAwait(false);
                })).ConfigureAwait(false);
            if (index >= 0) baseline[index] = firstByte;
        }
        for (var index = -5; index < 30; index++)
        {
            var started = Stopwatch.GetTimestamp();
            double firstByte = 0;
            await store.ReadAsync(Bucket, key, stat.Generation, async (content, token) =>
            {
                var buffer = new byte[1];
                Assert.AreEqual(1, await content.ReadAsync(buffer, token).ConfigureAwait(false));
                firstByte = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                await content.CopyToAsync(Stream.Null, token).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
            if (index >= 0) candidate[index] = firstByte;
        }
        await store.DeleteAsync(Bucket, key, CancellationToken.None).ConfigureAwait(false);
        Array.Sort(baseline);
        Array.Sort(candidate);
        return new TimeToFirstByteComparison(
            baseline[15],
            baseline[28],
            candidate[15],
            candidate[28]);
    }

    private static async Task<double> RestartProviderAsync(IObjectStore store)
    {
        var container = AssemblyHooks.Fixture.GetDependencyContainer(IntegrationDependency.ObjectStore);
        await container.StopAsync().ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        await container.StartAsync().ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await store.BucketExistsAsync(Bucket, timeout.Token).ConfigureAwait(false))
        {
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static async Task<RestoreMeasurement> MeasureRestoreAsync(IObjectStore store, string prefix)
    {
        var backupKey = prefix + "backup.bin";
        var restoredKey = prefix + "restored.bin";
        using var source = new PatternReadStream(W2Bytes);
        await store.PutAsync(Bucket, backupKey, source, W2Bytes, ContentType, CancellationToken.None).ConfigureAwait(false);
        var expected = source.GetSha256();
        var started = Stopwatch.GetTimestamp();
        await store.CopyAsync(Bucket, backupKey, restoredKey, CancellationToken.None).ConfigureAwait(false);
        var restored = await store.StatAsync(Bucket, restoredKey, CancellationToken.None).ConfigureAwait(false);
        var actual = await ReadCandidateSha256Async(store, restoredKey, restored.Generation, CancellationToken.None)
            .ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        CollectionAssert.AreEqual(expected, actual);
        await store.DeleteAsync(Bucket, backupKey, CancellationToken.None).ConfigureAwait(false);
        await store.DeleteAsync(Bucket, restoredKey, CancellationToken.None).ConfigureAwait(false);
        return new RestoreMeasurement(elapsed.TotalMilliseconds, restored.ContentLength, restored.ContentType);
    }

    private static async Task DeletePrefixAsync(MinioClient client, string prefix)
    {
        var keys = new List<string>();
        await foreach (var item in client.ListObjectsEnumAsync(
            new ListObjectsArgs().WithBucket(Bucket).WithPrefix(prefix).WithRecursive(true),
            CancellationToken.None))
        {
            keys.Add(item.Key);
        }
        await Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = 32 },
            async (key, token) => await client.RemoveObjectAsync(
                new RemoveObjectArgs().WithBucket(Bucket).WithObject(key), token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static void AssertNoMaterialRegression(WorkflowMeasurement baseline, WorkflowMeasurement candidate)
    {
        Assert.IsTrue(candidate.P95Milliseconds <= baseline.P95Milliseconds * 2 + 50);
        Assert.IsTrue(candidate.AllocatedBytes <= baseline.AllocatedBytes * 2 + 64L * 1024 * 1024);
        Assert.IsTrue(candidate.WorkingSetAfterBytes - candidate.WorkingSetBeforeBytes <= 256L * 1024 * 1024);
        Assert.IsTrue(candidate.Protocol.Requests <= baseline.Protocol.Requests + candidate.Operations);
    }

    private static async Task<ProviderResourceSnapshot> ReadProviderResourcesAsync()
    {
        var result = await AssemblyHooks.Fixture.GetDependencyContainer(IntegrationDependency.ObjectStore)
            .ExecAsync([
                "sh", "-c",
                "cpu=0; while read key value; do if [ \"$key\" = usage_usec ]; then cpu=$value; break; fi; done < /sys/fs/cgroup/cpu.stat; read memory < /sys/fs/cgroup/memory.current; read peak < /sys/fs/cgroup/memory.peak; printf '%s %s %s' \"$cpu\" \"$memory\" \"$peak\""
            ]).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("Object-storage provider resource sampling failed.");
        }
        var values = result.Stdout.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 3)
        {
            throw new InvalidOperationException("Object-storage provider resource sampling returned an invalid result.");
        }
        return new ProviderResourceSnapshot(
            long.Parse(values[0], CultureInfo.InvariantCulture),
            long.Parse(values[1], CultureInfo.InvariantCulture),
            long.Parse(values[2], CultureInfo.InvariantCulture));
    }

    private sealed class ObjectStoreSignalCollector : IDisposable
    {
        private readonly MeterListener _meterListener = new();
        private readonly ActivityListener _activityListener;
        private long _activeStreams;
        private long _peakActiveStreams;
        private long _activityCount;

        public ObjectStoreSignalCollector()
        {
            _meterListener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ObjectStoreTelemetry.MeterName)
                {
                    MetricNames.Add(instrument.Name);
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                if (instrument.Name == "skymonitor.central.object_storage.active_streams")
                {
                    var active = Interlocked.Add(ref _activeStreams, measurement);
                    long observed;
                    while (active > (observed = Interlocked.Read(ref _peakActiveStreams))
                        && Interlocked.CompareExchange(ref _peakActiveStreams, active, observed) != observed)
                    {
                    }
                }
            });
            _meterListener.SetMeasurementEventCallback<double>((_, _, _, _) => { });
            _meterListener.Start();
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ObjectStoreTelemetry.ActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    Interlocked.Increment(ref _activityCount);
                    lock (ActivityTagKeys)
                    {
                        foreach (var tag in activity.TagObjects) ActivityTagKeys.Add(tag.Key);
                    }
                }
            };
            ActivitySource.AddActivityListener(_activityListener);
        }

        public HashSet<string> MetricNames { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ActivityTagKeys { get; } = new(StringComparer.Ordinal);
        public long PeakActiveStreams => Interlocked.Read(ref _peakActiveStreams);
        public long ActivityCount => Interlocked.Read(ref _activityCount);

        public void Dispose()
        {
            _meterListener.Dispose();
            _activityListener.Dispose();
        }
    }

    private sealed record WorkflowMeasurement(
        string Name,
        int Operations,
        int Concurrency,
        double ElapsedMilliseconds,
        double MedianMilliseconds,
        double P95Milliseconds,
        double OperationsPerSecond,
        double CpuMilliseconds,
        long AllocatedBytes,
        long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes,
        ProtocolMeasurement Protocol,
        ProviderResourceDelta Provider);

    private sealed record ListMeasurement(
        double ElapsedMilliseconds,
        int Items,
        long Bytes,
        ProtocolMeasurement Protocol,
        ProviderResourceDelta Provider);

    private sealed record BacklogMeasurement(
        int InitialCount,
        long InitialBytes,
        double OldestAgeMilliseconds,
        double DrainMilliseconds,
        double ItemsPerSecond,
        int RemainingCount,
        ProtocolMeasurement Protocol,
        ProviderResourceDelta Provider);

    private sealed record TimeToFirstByteComparison(
        double BaselineMedianMilliseconds,
        double BaselineP95Milliseconds,
        double CandidateMedianMilliseconds,
        double CandidateP95Milliseconds);

    private sealed record ConcurrencyComparison(
        int Concurrency,
        WorkflowMeasurement Baseline,
        WorkflowMeasurement Candidate);

    private sealed record RestoreMeasurement(double ElapsedMilliseconds, long ContentLength, string ContentType);

    private sealed record ProviderResourceSnapshot(long CpuMicroseconds, long RssBytes, long PeakRssBytes);

    private sealed record ProviderResourceDelta(
        long CpuMicroseconds,
        long RssBeforeBytes,
        long RssAfterBytes,
        long PeakRssBytes)
    {
        public static ProviderResourceDelta Create(ProviderResourceSnapshot before, ProviderResourceSnapshot after)
            => new(
                Math.Max(0, after.CpuMicroseconds - before.CpuMicroseconds),
                before.RssBytes,
                after.RssBytes,
                after.PeakRssBytes);
    }
}
