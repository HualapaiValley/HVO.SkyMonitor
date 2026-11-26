using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Silo;

public sealed partial class SiloStockStoreTests
{
    private const int CaptureCount = 144;
    private const int RawLength = 6_996_992;
    private const int PreviewLength = 65_536;

    [TestMethod]
    [TestCategory("Performance")]
    [DataRow("off")]
    [DataRow("on")]
    public async Task MeasureStockSiloDurabilityCost(string sync)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        var access = RandomHex(12);
        var secret = RandomHex(24);
        var container = BuildStockContainer(access, secret, sync);
        await using var lease = container.ConfigureAwait(false);
        await container.StartAsync(token).ConfigureAwait(false);
        using var client = SiloS3ObjectStore.CreateClient(Endpoint(container), access, secret);
        await CreateVersionedBucketAsync(client, Bucket, token).ConfigureAwait(false);
        var descriptor = new SiloStoreDescriptor("local-silo-benchmark", Bucket, SiloStoreClass.Local, SiloRetrieval.Online,
            // Exercise the same publication algorithm in both runs. Off is never a qualified production profile.
            PersistenceQualified: true);
        var store = new SiloS3ObjectStore(client, descriptor, TimeSpan.FromSeconds(30));
        var directOne = await DirectAsync(store, workers: 1, token).ConfigureAwait(false);
        var directEight = await DirectAsync(store, workers: 8, token).ConfigureAwait(false);
        var staged = await PublishCaptureSampleAsync(store, token).ConfigureAwait(false);
        TestContext.WriteLine(FormattableString.Invariant(
            $"MINIO_FS_OSYNC={sync} direct_put_1_mbps={directOne.Put:F2} direct_put_8_mbps={directEight.Put:F2} exact_get_sha256_8_mbps={directEight.Get:F2} {staged}"));
    }

    private static async Task<(double Put, double Get)> DirectAsync(SiloS3ObjectStore store, int workers, CancellationToken token)
    {
        const int count = 24;
        var payload = new byte[RawLength];
        RandomNumberGenerator.Fill(payload);
        var requests = Enumerable.Range(0, count).Select(index => Put($"benchmark/direct/{workers}/{index}", payload)).ToArray();
        var versions = new string[count];
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token };
        var started = Stopwatch.GetTimestamp();
        await Parallel.ForEachAsync(Enumerable.Range(0, count), parallel, async (index, cancellationToken) =>
        {
            var result = await store.PutCreateOnlyAsync(requests[index], cancellationToken).ConfigureAwait(false);
            Assert.IsTrue(result.Created);
            versions[index] = result.VersionId!;
        }).ConfigureAwait(false);
        var put = MegabitsPerSecond(count * (long)RawLength, Stopwatch.GetElapsedTime(started));
        started = Stopwatch.GetTimestamp();
        await Parallel.ForEachAsync(Enumerable.Range(0, count), parallel, (index, cancellationToken) =>
            new ValueTask(VerifyAsync(store, store.Descriptor, versions[index], requests[index], cancellationToken))).ConfigureAwait(false);
        return (put, MegabitsPerSecond(count * (long)RawLength, Stopwatch.GetElapsedTime(started)));
    }

    private static async Task<string> PublishCaptureSampleAsync(SiloS3ObjectStore store, CancellationToken token)
    {
        var rootPath = Directory.CreateTempSubdirectory("silo-cost-").FullName;
        try
        {
            using var root = SiloStateRoot.Acquire(rootPath);
            var binding = new SiloStorageBinding("silo-benchmark", store.Descriptor.StoreId, store.Descriptor.Bucket);
            var clock = TimeProvider.System;
            var state = new SiloPublicationState(root.PublicationDatabasePath, binding, clock);
            await state.InitializeAsync(token).ConfigureAwait(false);
            using var index = new SiloCatalogIndex(root.IndexDatabasePath, binding, clock);
            await index.InitializeAsync(token).ConfigureAwait(false);
            var counted = new CountedStore(store);
            var catalog = new SiloCatalog(index, new SiloStoreSet(counted), SiloNoAuthorityEvidence.Instance);
            using var journal = new SiloJournalWriter(state, root.Spool, counted, binding, clock);
            var spool = new SiloFilePayloadSpool(root, 32L * 1024 * 1024, 16);
            using var publisher = new SiloPublisher(state, root.Spool, journal, catalog, spool, binding, clock);
            await journal.OpenFirstGenerationAsync(new Dictionary<string, long> { ["capture-sequence"] = 0 }, token).ConfigureAwait(false);
            await catalog.CatchUpAsync(await state.ReadWriterEvidenceAsync(token).ConfigureAwait(false), null, token).ConfigureAwait(false);
            counted.Puts = 0;
            counted.Gets = 0;

            var raw = new byte[RawLength];
            var preview = new byte[PreviewLength];
            RandomNumberGenerator.Fill(raw);
            RandomNumberGenerator.Fill(preview);
            var latencies = new double[CaptureCount];
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var started = Stopwatch.GetTimestamp();
            for (var capture = 0; capture < CaptureCount; capture++)
            {
                var groupStart = Stopwatch.GetTimestamp();
                BinaryPrimitives.WriteInt32LittleEndian(raw, capture);
                BinaryPrimitives.WriteInt32LittleEndian(preview, capture);
                await PublishSampleAsync(publisher, raw, preview, capture, token).ConfigureAwait(false);
                latencies[capture] = Stopwatch.GetElapsedTime(groupStart).TotalMilliseconds;
            }
            var elapsed = Stopwatch.GetElapsedTime(started);
            Array.Sort(latencies);
            process.Refresh();
            var bytes = CaptureCount * (RawLength + (long)PreviewLength);
            return FormattableString.Invariant($"committed_mbps={MegabitsPerSecond(bytes, elapsed):F2} puts={counted.Puts} gets={counted.Gets} p95_ms={Quantile(latencies, 0.95):F2} p99_ms={Quantile(latencies, 0.99):F2} cpu_s={(process.TotalProcessorTime - cpuBefore).TotalSeconds:F2} peak_mib={process.PeakWorkingSet64 / 1048576.0:F2}");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static async Task PublishSampleAsync(SiloPublisher publisher, byte[] raw, byte[] preview, int capture, CancellationToken token)
    {
        var captureId = capture.ToString(CultureInfo.InvariantCulture);
        var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(capture);
        var rawMember = BenchmarkMember(captureId, "raw", raw, SiloRetentionClass.Full);
        var previewMember = BenchmarkMember(captureId, "preview", preview, SiloRetentionClass.Display);
        var draft = new SiloGroupDraft("sample-capture", captureId, "1", timestamp, timestamp, timestamp.AddSeconds(1),
            [rawMember, previewMember], [], new([]), Capture: new("silo-benchmark", capture + 1));
        await publisher.StageAsync(draft, (member, _) => ValueTask.FromResult<Stream>(
            new MemoryStream(member.Role == "raw" ? raw : preview, writable: false)), token).ConfigureAwait(false);
        var outcome = await publisher.PublishAsync(draft.GroupId, draft.RevisionId, token).ConfigureAwait(false);
        Assert.AreEqual(SiloStagedState.Visible, outcome.State, outcome.BlockedCode);
    }

    private static SiloDraftMember BenchmarkMember(string captureId, string role, byte[] bytes, SiloRetentionClass retentionClass)
    {
        var sha = SiloHash.Sha256(bytes);
        return new($"{captureId}-{role}", "1", role, retentionClass, "application/octet-stream", bytes.LongLength, sha, true,
            SiloFilePayloadSpool.TokenOf(sha));
    }

    private static double MegabitsPerSecond(long bytes, TimeSpan elapsed) => bytes * 8.0 / 1_000_000 / elapsed.TotalSeconds;

    private static double Quantile(double[] sorted, double percentile) => sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];

    private sealed class CountedStore(SiloS3ObjectStore store) : ISiloObjectStore
    {
        public int Puts { get; set; }

        public int Gets { get; set; }

        public SiloStoreDescriptor Descriptor => store.Descriptor;

        public ValueTask<SiloPutResult> PutCreateOnlyAsync(SiloPutRequest request, CancellationToken cancellationToken)
        {
            Puts++;
            return store.PutCreateOnlyAsync(request, cancellationToken);
        }

        public ValueTask<SiloOpenResult> OpenVersionAsync(string key, string versionId, CancellationToken cancellationToken)
        {
            Gets++;
            return store.OpenVersionAsync(key, versionId, cancellationToken);
        }

        public ValueTask<string?> LocateCurrentVersionAsync(string key, CancellationToken cancellationToken) =>
            store.LocateCurrentVersionAsync(key, cancellationToken);

        public IAsyncEnumerable<string> ListKeysAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
            store.ListKeysAsync(prefix, startAfter, cancellationToken);
    }
}
