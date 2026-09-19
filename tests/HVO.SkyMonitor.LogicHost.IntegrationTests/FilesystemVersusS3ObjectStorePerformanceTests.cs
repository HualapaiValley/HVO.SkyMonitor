using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Issue #585 acceptance: the filesystem provider measured against the S3 path on the
/// canonical object-store workloads, through the same <see cref="IObjectStore"/> contract,
/// in the same process, with the same payload generator, so that the only variable is the
/// provider. Records CPU, allocations, working set, filesystem read/write bytes and syscalls
/// (from <c>/proc/self/io</c>, which the S3 path cannot expose for its remote side), latency
/// median and p95 over 30+ operations, throughput, and the W3P backlog drain including a
/// provider restart between seed and drain.
/// </summary>
/// <remarks>
/// The comparison is deliberately asymmetric in what it proves. S3 here is a MinIO
/// Testcontainer on the same host over loopback, which is the most favourable S3 can be; a
/// real deployment adds a network. The filesystem provider hashes every byte it writes and
/// every byte it reads, and fsyncs data, descriptor and directory on every publish; the S3
/// path does none of that locally. The assertion is therefore not "faster than S3" but the
/// issue's own rule: no unexplained material regression against the S3 path, with the
/// explanation for any regression recorded in the evidence.
/// </remarks>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class FilesystemVersusS3ObjectStorePerformanceTests
{
    private const string Bucket = "skymonitor-artifacts";
    private const string ContentType = "application/x-skymonitor-frame";
    private const int W1Bytes = 4_708_352;
    private const int W2Bytes = 12_879_360;
    private static readonly JsonSerializerOptions EvidenceJson = new() { WriteIndented = true };

    [TestMethod]
    [Timeout(1_800_000)]
    public async Task CanonicalMatrix_FilesystemAgainstS3()
    {
        var runId = Guid.NewGuid().ToString("N");
        var fixture = AssemblyHooks.Fixture;
        var fixtureClient = fixture.Factory.Services.GetRequiredService<IMinioClient>();
        if (!await fixtureClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket)).ConfigureAwait(false))
        {
            await fixtureClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket)).ConfigureAwait(false);
        }

        var s3Options = new CentralObjectStorageOptions
        {
            Provider = ObjectStorageProvider.S3,
            ServiceEndpoint = fixture.MinioEndpoint,
            Region = "us-east-1",
            UseTls = false,
            AddressingStyle = ObjectStorageAddressingStyle.Path,
            CredentialMode = ObjectStorageCredentialMode.Static,
            AccessKey = IntegrationTestFixture.MinioAccessKey,
            SecretKey = IntegrationTestFixture.MinioSecretKey
        };
        using var s3Client = S3ObjectStoreClientFactory.Create(s3Options);
        var s3 = ObjectStoreTestClient.Create(s3Client, s3Options);

        var root = Path.Combine(Path.GetTempPath(), "hvo-585-perf-" + runId);
        Directory.CreateDirectory(Path.Combine(root, Bucket));
        Directory.CreateDirectory(Path.Combine(root, "skymonitor-diagnostics"));
        var filesystem = OpenFilesystem(root);
        var storage = DescribeStorage(root);

        var prefix = $"issue-585/{runId}/";
        try
        {
            var w1 = await CompareAsync("W1", s3, filesystem, prefix + "w1/", W1Bytes, warmups: 5, operations: 30, concurrency: 1).ConfigureAwait(false);
            var w2 = await CompareAsync("W2", s3, filesystem, prefix + "w2/", W2Bytes, warmups: 5, operations: 30, concurrency: 1).ConfigureAwait(false);

            var w3mPrefix = prefix + "w3m/";
            await SeedAsync(s3, w3mPrefix, 10_000, payloadBytes: 1, concurrency: 32).ConfigureAwait(false);
            await SeedAsync(filesystem, w3mPrefix, 10_000, payloadBytes: 1, concurrency: 32).ConfigureAwait(false);
            var w3m = new
            {
                Records = 10_000,
                S3 = await MeasureListAsync(s3, w3mPrefix, 10_000).ConfigureAwait(false),
                Filesystem = await MeasureListAsync(filesystem, w3mPrefix, 10_000).ConfigureAwait(false)
            };

            var w3pS3 = prefix + "w3p/s3/";
            var w3pFs = prefix + "w3p/fs/";
            await SeedAsync(s3, w3pS3, 100, W2Bytes, concurrency: 8).ConfigureAwait(false);
            await SeedAsync(filesystem, w3pFs, 100, W2Bytes, concurrency: 8).ConfigureAwait(false);
            var w3pS3Drain = await MeasureDrainAsync(s3, w3pS3, 100, W2Bytes).ConfigureAwait(false);
            // Provider restart for the filesystem path: a new store instance over the same root,
            // with a reconciliation pass first, exactly what a LogicHost restart does.
            var restartStarted = Stopwatch.GetTimestamp();
            filesystem = OpenFilesystem(root);
            var reconciliation = new FilesystemObjectReconciler(filesystem, TimeProvider.System, NullLogger<FilesystemObjectReconciler>.Instance)
                .Reconcile(Bucket, new FilesystemReconciliationOptions(), CancellationToken.None);
            var restartMilliseconds = Stopwatch.GetElapsedTime(restartStarted).TotalMilliseconds;
            var w3pFsDrain = await MeasureDrainAsync(filesystem, w3pFs, 100, W2Bytes).ConfigureAwait(false);
            var w3p = new { Payloads = 100, PayloadBytes = W2Bytes, S3 = w3pS3Drain, Filesystem = w3pFsDrain, FilesystemRestartMilliseconds = restartMilliseconds, ReconciliationLiveObjects = reconciliation.LiveObjects };

            var w4 = new List<object>();
            foreach (var concurrency in new[] { 1, 4, 8 })
            {
                w4.Add(await CompareAsync($"W4-c{concurrency}", s3, filesystem, $"{prefix}w4/c{concurrency}/", W1Bytes, warmups: 20, operations: 200, concurrency).ConfigureAwait(false));
            }

            var bucketBytesAfter = Directory.EnumerateFiles(Path.Combine(root, Bucket), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "local-uncommitted";
            var evidence = new
            {
                Schema = "hvo-issue-585-filesystem-vs-s3-performance-v1",
                Revision = revision,
                RunId = runId,
                Environment = new
                {
                    OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    Processors = Environment.ProcessorCount,
                    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    Configuration = IsRelease ? "Release" : "Debug",
                    FilesystemRoot = root,
                    Storage = storage,
                    S3 = "MinIO Testcontainer on loopback (most favourable S3 topology; a real deployment adds a network)"
                },
                Method = "Same IObjectStore contract, same process, same PatternReadStream payload generator, same fixture; per workload: warm-ups discarded, then N measured operations; latency per operation via Stopwatch; CPU via Process.TotalProcessorTime; allocations via GC.GetTotalAllocatedBytes(precise); working set via Process.WorkingSet64; filesystem bytes/syscalls via /proc/self/io deltas (rchar/wchar/syscr/syscw/read_bytes/write_bytes) which capture this process's own I/O for both providers (for S3 that is the socket traffic; for the filesystem it is the data, descriptor, temporary and fsync work).",
                Rule = "Issue #585: no unexplained material regression against the S3 path. Explanation recorded per workload: the filesystem provider hashes every byte written and read and fsyncs data, descriptor and directory per publish; S3 does none of that locally.",
                Workloads = new { W1 = w1, W2 = w2, W3M = w3m, W3P = w3p, W4 = w4 },
                FilesystemBucketBytesAfterRun = bucketBytesAfter,
                Correctness = "Every workflow verified SHA-256 of staged and canonical copies against the generator on both providers; W3M asserted 10,000 distinct keys in ordinal order; W3P asserted 100 payloads of exact length drained; any mismatch fails the test."
            };
            var directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "issue-585");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"filesystem-vs-s3-{runId}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, EvidenceJson)).ConfigureAwait(false);
            Console.WriteLine($"issue-585 evidence: {path}");
            Console.WriteLine(Summarize("W1", w1));
            Console.WriteLine(Summarize("W2", w2));
            Console.WriteLine($"W3M list 10,000: s3={w3m.S3.ElapsedMilliseconds:F0}ms fs={w3m.Filesystem.ElapsedMilliseconds:F0}ms");
            Console.WriteLine($"W3P drain 100xW2: s3={w3pS3Drain.DrainMilliseconds:F0}ms fs={w3pFsDrain.DrainMilliseconds:F0}ms fsRestart={restartMilliseconds:F0}ms");
            foreach (var entry in w4)
            {
                Console.WriteLine(Summarize("W4", entry));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [Timeout(1_800_000)]
    public async Task CanonicalWriteMatrix_HardLinksAgainstStreamingCopy()
    {
        var runId = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "hvo-920-perf-" + runId);
        var streamingRoot = Path.Combine(root, "streaming");
        var linkedRoot = Path.Combine(root, "hard-link");
        foreach (var storeRoot in new[] { streamingRoot, linkedRoot })
        {
            Directory.CreateDirectory(Path.Combine(storeRoot, Bucket));
            Directory.CreateDirectory(Path.Combine(storeRoot, "skymonitor-diagnostics"));
        }
        var streaming = OpenFilesystem(streamingRoot);
        streaming.DisableHardLinksForTest = true;
        var linked = OpenFilesystem(linkedRoot);
        var prefix = $"issue-920/{runId}/";
        try
        {
            var w1 = await CompareFilesystemStrategiesAsync("W1", streaming, linked, prefix + "w1/", W1Bytes, 5, 30, 1).ConfigureAwait(false);
            var w2 = await CompareFilesystemStrategiesAsync("W2", streaming, linked, prefix + "w2/", W2Bytes, 5, 30, 1).ConfigureAwait(false);
            var w4 = new List<object>();
            foreach (var concurrency in new[] { 1, 4, 8 })
            {
                w4.Add(await CompareFilesystemStrategiesAsync(
                    $"W4-c{concurrency}", streaming, linked, $"{prefix}w4/c{concurrency}/", W1Bytes, 20, 200, concurrency).ConfigureAwait(false));
            }
            var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "local-uncommitted";
            var evidence = new
            {
                Schema = "hvo-issue-920-hard-link-copy-performance-v1",
                Revision = revision,
                RunId = runId,
                Environment = new
                {
                    OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    Processors = Environment.ProcessorCount,
                    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    Configuration = IsRelease ? "Release" : "Debug",
                    Storage = DescribeStorage(root)
                },
                Method = "Same process, filesystem and IObjectStore workflow. Baseline forces the streaming-copy fallback; candidate uses descriptor-relative hard links. Each cell discards warm-ups and measures independent put, digest-read, copy, digest-read and delete workflows. Metrics are Stopwatch latency, process CPU, managed allocations, RSS and /proc/self/io deltas.",
                Workloads = new { W1 = w1, W2 = w2, W4 = w4 },
                Correctness = "Every operation verifies SHA-256 after put and copy, exact content length/type, independent generation tokens, and deletes both logical objects."
            };
            var directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "issue-920");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"hard-link-vs-streaming-{runId}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, EvidenceJson)).ConfigureAwait(false);
            Console.WriteLine($"issue-920 evidence: {path}");
            Console.WriteLine(SummarizeStrategies("W1", w1));
            Console.WriteLine(SummarizeStrategies("W2", w2));
            foreach (var entry in w4)
            {
                Console.WriteLine(SummarizeStrategies("W4", entry));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static bool IsRelease
    {
        get
        {
#if DEBUG
            return false;
#else
            return true;
#endif
        }
    }

    private static FilesystemObjectStore OpenFilesystem(string root)
    {
        var options = new CentralObjectStorageOptions { Provider = ObjectStorageProvider.Filesystem, ArtifactBucket = Bucket, DiagnosticsBucket = "skymonitor-diagnostics" };
        options.Filesystem.Root = root;
        var wrapped = Options.Create(options);
        return new FilesystemObjectStore(wrapped, new ObjectStoreTelemetry(wrapped), TimeProvider.System, NullLogger<FilesystemObjectStore>.Instance);
    }

    private static string DescribeStorage(string root)
    {
        try
        {
            var mount = new DriveInfo(root);
            return $"{mount.DriveFormat} at {mount.Name} ({mount.AvailableFreeSpace / (1024 * 1024)} MiB free)";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "unknown";
        }
    }

    private static async Task<object> CompareAsync(string name, IObjectStore s3, IObjectStore filesystem, string prefix, int payloadBytes, int warmups, int operations, int concurrency)
    {
        var s3Measurement = await MeasureAsync($"{name}-s3", warmups, operations, concurrency,
            (i, ct) => ExecuteWorkflowAsync(s3, $"{prefix}s3/{i:D4}", payloadBytes, ct)).ConfigureAwait(false);
        var fsMeasurement = await MeasureAsync($"{name}-filesystem", warmups, operations, concurrency,
            (i, ct) => ExecuteWorkflowAsync(filesystem, $"{prefix}fs/{i:D4}", payloadBytes, ct)).ConfigureAwait(false);
        return new
        {
            Workload = name,
            PayloadBytes = payloadBytes,
            Warmups = warmups,
            Operations = operations,
            Concurrency = concurrency,
            S3 = s3Measurement,
            Filesystem = fsMeasurement,
            Change = new
            {
                MedianLatency = Ratio(fsMeasurement.MedianMilliseconds, s3Measurement.MedianMilliseconds),
                P95Latency = Ratio(fsMeasurement.P95Milliseconds, s3Measurement.P95Milliseconds),
                Throughput = Ratio(fsMeasurement.OperationsPerSecond, s3Measurement.OperationsPerSecond),
                Cpu = Ratio(fsMeasurement.CpuMilliseconds, s3Measurement.CpuMilliseconds),
                Allocations = Ratio(fsMeasurement.AllocatedBytes, s3Measurement.AllocatedBytes),
                WorkingSetGrowthBytes = (fsMeasurement.WorkingSetAfterBytes - fsMeasurement.WorkingSetBeforeBytes) - (s3Measurement.WorkingSetAfterBytes - s3Measurement.WorkingSetBeforeBytes)
            }
        };
    }

    private static async Task<object> CompareFilesystemStrategiesAsync(
        string name,
        IObjectStore streaming,
        IObjectStore linked,
        string prefix,
        int payloadBytes,
        int warmups,
        int operations,
        int concurrency)
    {
        var baseline = await MeasureAsync($"{name}-streaming", warmups, operations, concurrency,
            (i, ct) => ExecuteWorkflowAsync(streaming, $"{prefix}streaming/{i:D4}", payloadBytes, ct)).ConfigureAwait(false);
        var candidate = await MeasureAsync($"{name}-hard-link", warmups, operations, concurrency,
            (i, ct) => ExecuteWorkflowAsync(linked, $"{prefix}hard-link/{i:D4}", payloadBytes, ct)).ConfigureAwait(false);
        return new
        {
            Workload = name,
            PayloadBytes = payloadBytes,
            Warmups = warmups,
            Operations = operations,
            Concurrency = concurrency,
            Streaming = baseline,
            HardLink = candidate,
            Change = new
            {
                MedianLatency = Ratio(candidate.MedianMilliseconds, baseline.MedianMilliseconds),
                P95Latency = Ratio(candidate.P95Milliseconds, baseline.P95Milliseconds),
                Throughput = Ratio(candidate.OperationsPerSecond, baseline.OperationsPerSecond),
                Cpu = Ratio(candidate.CpuMilliseconds, baseline.CpuMilliseconds),
                Allocations = Ratio(candidate.AllocatedBytes, baseline.AllocatedBytes),
                WriteBytes = baseline.Io.WriteBytes <= 0 ? "n/a" : Ratio(candidate.Io.WriteBytes, baseline.Io.WriteBytes),
                WriteSyscalls = baseline.Io.WriteSyscalls <= 0 ? "n/a" : Ratio(candidate.Io.WriteSyscalls, baseline.Io.WriteSyscalls)
            }
        };
    }

    private static string Ratio(double candidate, double baseline)
        => baseline <= 0 ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{candidate / baseline:F2}x");

    private static string Summarize(string name, object comparison)
    {
        var json = JsonSerializer.SerializeToElement(comparison);
        var s3 = json.GetProperty("S3");
        var fs = json.GetProperty("Filesystem");
        var change = json.GetProperty("Change");
        return $"{name} c{json.GetProperty("Concurrency").GetInt32()} {json.GetProperty("PayloadBytes").GetInt32() / 1024 / 1024}MiB: " +
               $"median s3={s3.GetProperty("MedianMilliseconds").GetDouble():F1}ms fs={fs.GetProperty("MedianMilliseconds").GetDouble():F1}ms ({change.GetProperty("MedianLatency")}) " +
               $"p95 s3={s3.GetProperty("P95Milliseconds").GetDouble():F1}ms fs={fs.GetProperty("P95Milliseconds").GetDouble():F1}ms ({change.GetProperty("P95Latency")}) " +
               $"ops/s s3={s3.GetProperty("OperationsPerSecond").GetDouble():F2} fs={fs.GetProperty("OperationsPerSecond").GetDouble():F2} " +
               $"cpu {change.GetProperty("Cpu")} alloc {change.GetProperty("Allocations")} " +
               $"fsWrite={fs.GetProperty("Io").GetProperty("WriteBytes").GetInt64() / 1024 / 1024}MiB fsRead={fs.GetProperty("Io").GetProperty("ReadBytes").GetInt64() / 1024 / 1024}MiB";
    }

    private static string SummarizeStrategies(string name, object comparison)
    {
        var json = JsonSerializer.SerializeToElement(comparison);
        var streaming = json.GetProperty("Streaming");
        var linked = json.GetProperty("HardLink");
        var change = json.GetProperty("Change");
        return $"{name} c{json.GetProperty("Concurrency").GetInt32()} {json.GetProperty("PayloadBytes").GetInt32() / 1024 / 1024}MiB: " +
               $"median stream={streaming.GetProperty("MedianMilliseconds").GetDouble():F1}ms link={linked.GetProperty("MedianMilliseconds").GetDouble():F1}ms ({change.GetProperty("MedianLatency")}) " +
               $"p95 stream={streaming.GetProperty("P95Milliseconds").GetDouble():F1}ms link={linked.GetProperty("P95Milliseconds").GetDouble():F1}ms ({change.GetProperty("P95Latency")}) " +
               $"ops/s stream={streaming.GetProperty("OperationsPerSecond").GetDouble():F2} link={linked.GetProperty("OperationsPerSecond").GetDouble():F2} " +
               $"cpu {change.GetProperty("Cpu")} alloc {change.GetProperty("Allocations")} writes {change.GetProperty("WriteBytes")}";
    }

    private static async Task<Measurement> MeasureAsync(string name, int warmups, int operations, int concurrency, Func<int, CancellationToken, Task> workflow)
    {
        await Parallel.ForEachAsync(Enumerable.Range(-warmups, warmups), new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (i, ct) => await workflow(i, ct).ConfigureAwait(false)).ConfigureAwait(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var rssBefore = process.WorkingSet64;
        var ioBefore = ProcessIo.Read();
        var latencies = new double[operations];
        var started = Stopwatch.GetTimestamp();
        await Parallel.ForEachAsync(Enumerable.Range(0, operations), new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (i, ct) =>
            {
                var operationStarted = Stopwatch.GetTimestamp();
                await workflow(i, ct).ConfigureAwait(false);
                latencies[i] = Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds;
            }).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var ioAfter = ProcessIo.Read();
        Array.Sort(latencies);
        return new Measurement(
            name, operations, concurrency, elapsed.TotalMilliseconds,
            latencies[operations / 2],
            latencies[(int)Math.Ceiling(operations * 0.95) - 1],
            latencies[^1],
            operations / elapsed.TotalSeconds,
            process.TotalProcessorTime.TotalMilliseconds - cpuBefore.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore,
            rssBefore, process.WorkingSet64,
            ProcessIo.Delta(ioBefore, ioAfter));
    }

    /// <summary>Put staged, verify digest, copy to canonical, verify digest, delete both. Same as the S3 canonical harness.</summary>
    private static async Task ExecuteWorkflowAsync(IObjectStore store, string key, int payloadBytes, CancellationToken cancellationToken)
    {
        var stagingKey = key + ".staging";
        var canonicalKey = key + ".canonical";
        using var source = new PatternReadStream(payloadBytes);
        await store.PutAsync(Bucket, stagingKey, source, payloadBytes, ContentType, cancellationToken).ConfigureAwait(false);
        var expected = source.GetSha256();
        var staged = await store.StatAsync(Bucket, stagingKey, cancellationToken).ConfigureAwait(false);
        CollectionAssert.AreEqual(expected, await ReadSha256Async(store, stagingKey, staged.Generation, cancellationToken).ConfigureAwait(false));
        await store.CopyAsync(Bucket, stagingKey, canonicalKey, cancellationToken).ConfigureAwait(false);
        var canonical = await store.StatAsync(Bucket, canonicalKey, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(payloadBytes, canonical.ContentLength);
        Assert.AreEqual(ContentType, canonical.ContentType);
        CollectionAssert.AreEqual(expected, await ReadSha256Async(store, canonicalKey, canonical.Generation, cancellationToken).ConfigureAwait(false));
        await store.DeleteAsync(Bucket, stagingKey, cancellationToken).ConfigureAwait(false);
        await store.DeleteAsync(Bucket, canonicalKey, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadSha256Async(IObjectStore store, string key, string generation, CancellationToken cancellationToken)
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

    private static async Task SeedAsync(IObjectStore store, string prefix, int count, int payloadBytes, int concurrency)
    {
        await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (i, ct) =>
        {
            using var source = new PatternReadStream(payloadBytes);
            await store.PutAsync(Bucket, $"{prefix}{i:D5}", source, payloadBytes, ContentType, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static async Task<ListMeasurement> MeasureListAsync(IObjectStore store, string prefix, int expectedCount)
    {
        var ioBefore = ProcessIo.Read();
        var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var keys = new List<string>(expectedCount);
        await foreach (var item in store.ListAsync(Bucket, prefix, CancellationToken.None))
        {
            keys.Add(item.Key);
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.HasCount(expectedCount, keys);
        CollectionAssert.AreEqual(keys.Order(StringComparer.Ordinal).ToArray(), keys.ToArray());
        Assert.HasCount(expectedCount, keys.Distinct(StringComparer.Ordinal));
        return new ListMeasurement(elapsed.TotalMilliseconds, keys.Count, GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore, ProcessIo.Delta(ioBefore, ProcessIo.Read()));
    }

    private static async Task<DrainMeasurement> MeasureDrainAsync(IObjectStore store, string prefix, int count, int payloadBytes)
    {
        var ioBefore = ProcessIo.Read();
        var started = Stopwatch.GetTimestamp();
        var drained = 0;
        long bytes = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (i, ct) =>
        {
            var key = $"{prefix}{i:D5}";
            var stat = await store.StatAsync(Bucket, key, ct).ConfigureAwait(false);
            Assert.AreEqual(payloadBytes, stat.ContentLength);
            long read = 0;
            await store.ReadAsync(Bucket, key, stat.Generation, async (content, token) =>
            {
                var buffer = GC.AllocateUninitializedArray<byte>(128 * 1024);
                int n;
                while ((n = await content.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    read += n;
                }
            }, ct).ConfigureAwait(false);
            Assert.AreEqual(payloadBytes, read);
            await store.DeleteAsync(Bucket, key, ct).ConfigureAwait(false);
            Interlocked.Increment(ref drained);
            Interlocked.Add(ref bytes, read);
        }).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.AreEqual(count, drained);
        return new DrainMeasurement(elapsed.TotalMilliseconds, drained, bytes, bytes / elapsed.TotalSeconds / (1024 * 1024), ProcessIo.Delta(ioBefore, ProcessIo.Read()));
    }

    private sealed record Measurement(
        string Name, int Operations, int Concurrency, double ElapsedMilliseconds,
        double MedianMilliseconds, double P95Milliseconds, double MaxMilliseconds, double OperationsPerSecond,
        double CpuMilliseconds, long AllocatedBytes, long WorkingSetBeforeBytes, long WorkingSetAfterBytes, ProcessIo Io);

    private sealed record ListMeasurement(double ElapsedMilliseconds, int Count, long AllocatedBytes, ProcessIo Io);

    private sealed record DrainMeasurement(double DrainMilliseconds, int Drained, long Bytes, double MebibytesPerSecond, ProcessIo Io);

    /// <summary>Linux /proc/self/io counters; zero elsewhere.</summary>
    private sealed record ProcessIo(long ReadChars, long WriteChars, long ReadSyscalls, long WriteSyscalls, long ReadBytes, long WriteBytes)
    {
        public static ProcessIo Read()
        {
            if (!OperatingSystem.IsLinux())
            {
                return new(0, 0, 0, 0, 0, 0);
            }
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines("/proc/self/io"))
            {
                var parts = line.Split(':', 2);
                if (parts.Length == 2 && long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    values[parts[0]] = value;
                }
            }
            return new(values.GetValueOrDefault("rchar"), values.GetValueOrDefault("wchar"), values.GetValueOrDefault("syscr"), values.GetValueOrDefault("syscw"), values.GetValueOrDefault("read_bytes"), values.GetValueOrDefault("write_bytes"));
        }

        public static ProcessIo Delta(ProcessIo before, ProcessIo after)
            => new(after.ReadChars - before.ReadChars, after.WriteChars - before.WriteChars, after.ReadSyscalls - before.ReadSyscalls, after.WriteSyscalls - before.WriteSyscalls, after.ReadBytes - before.ReadBytes, after.WriteBytes - before.WriteBytes);
    }

    private sealed class PatternReadStream(long length) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _position;
        private byte[]? _sha256;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public byte[] GetSha256() => _sha256 ??= _hash.GetHashAndReset();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - _position;
            if (remaining <= 0)
            {
                return 0;
            }
            var n = (int)Math.Min(count, remaining);
            for (var i = 0; i < n; i++)
            {
                buffer[offset + i] = (byte)((_position + i) * 31 + ((_position + i) >> 8));
            }
            _hash.AppendData(buffer, offset, n);
            _position += n;
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer, offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var array = new byte[buffer.Length];
            var n = Read(array, 0, array.Length);
            array.AsSpan(0, n).CopyTo(buffer.Span);
            return ValueTask.FromResult(n);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
