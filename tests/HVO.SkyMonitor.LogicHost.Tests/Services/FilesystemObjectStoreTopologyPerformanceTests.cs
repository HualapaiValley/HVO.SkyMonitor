using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Tests.Services;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class FilesystemObjectStoreTopologyPerformanceTests
{
    private const int W1Bytes = 4_708_352;
    private const int W2Bytes = 12_879_360;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string W1Sha256 = CreatePatternSha256(W1Bytes);
    private static readonly string W2Sha256 = CreatePatternSha256(W2Bytes);

    [TestMethod]
    [Timeout(1_800_000)]
    public async Task ExactMount_RecordsCanonicalTopologyEvidence()
    {
        var root = Environment.GetEnvironmentVariable("HVO_QUALIFICATION_ROOT")
            ?? throw new AssertInconclusiveException("HVO_QUALIFICATION_ROOT is required.");
        var evidenceRoot = Environment.GetEnvironmentVariable("HVO_QUALIFICATION_EVIDENCE")
            ?? throw new AssertInconclusiveException("HVO_QUALIFICATION_EVIDENCE is required.");
        var runId = Guid.NewGuid().ToString("N");
        var artifactBucket = "qualification-artifacts";
        var diagnosticsBucket = "qualification-diagnostics";
        var artifactPath = Path.Combine(root, artifactBucket);
        var diagnosticsPath = Path.Combine(root, diagnosticsBucket);
        Directory.CreateDirectory(artifactPath);
        Directory.CreateDirectory(diagnosticsPath);
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
            File.SetUnixFileMode(artifactPath, mode);
            File.SetUnixFileMode(diagnosticsPath, mode);
        }
        var store = OpenStore(root, artifactBucket, diagnosticsBucket);
        try
        {
            var w1 = await MeasureAsync("W1", store, artifactBucket, W1Bytes, 5, 30, 1).ConfigureAwait(false);
            var w2 = await MeasureAsync("W2", store, artifactBucket, W2Bytes, 5, 30, 1).ConfigureAwait(false);
            var w4 = new List<Measurement>();
            foreach (var concurrency in new[] { 1, 4, 8 })
            {
                w4.Add(await MeasureAsync($"W4-c{concurrency}", store, artifactBucket, W1Bytes, 20, 200, concurrency).ConfigureAwait(false));
            }

            const int metadataCount = 10_000;
            await SeedAsync(store, artifactBucket, "w3m/", metadataCount, 1, 32).ConfigureAwait(false);
            var list = await MeasureActivityAsync(async () =>
            {
                var keys = new List<string>(metadataCount);
                await foreach (var item in store.ListAsync(artifactBucket, "w3m/", CancellationToken.None)) keys.Add(item.Key);
                CollectionAssert.AreEqual(Enumerable.Range(0, metadataCount).Select(i => $"w3m/{i:D5}").ToArray(), keys.ToArray());
                return keys.Count;
            }).ConfigureAwait(false);

            const int backlogCount = 100;
            await SeedAsync(store, artifactBucket, "w3p/", backlogCount, W2Bytes, 8).ConfigureAwait(false);
            store.Dispose();
            FilesystemObjectStore? reopened = null;
            var restart = await MeasureActivityAsync(() =>
            {
                reopened = OpenStore(root, artifactBucket, diagnosticsBucket);
                return Task.FromResult(new FilesystemObjectReconciler(reopened, TimeProvider.System, NullLogger<FilesystemObjectReconciler>.Instance)
                    .Reconcile(artifactBucket, new FilesystemReconciliationOptions(), CancellationToken.None));
            }).ConfigureAwait(false);
            store = reopened!;
            long drainedBytes = 0;
            var drain = await MeasureActivityAsync(async () =>
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, backlogCount), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (i, ct) =>
                {
                    var key = $"w3p/{i:D5}";
                    Interlocked.Add(ref drainedBytes, await ReadAndVerifyAsync(store, artifactBucket, key, W2Bytes, ct).ConfigureAwait(false));
                    await store.DeleteAsync(artifactBucket, key, ct).ConfigureAwait(false);
                }).ConfigureAwait(false);
                return drainedBytes;
            }).ConfigureAwait(false);

            // Remove the metadata set before backup so the timed backup is W1/W2-sized data, not 10k tiny descriptors.
            for (var i = 0; i < metadataCount; i++)
            {
                await store.DeleteAsync(artifactBucket, $"w3m/{i:D5}", CancellationToken.None).ConfigureAwait(false);
            }
            using (var source = new PatternStream(W2Bytes))
            {
                await store.PutAsync(artifactBucket, "backup/w2", source, W2Bytes, "application/octet-stream", CancellationToken.None).ConfigureAwait(false);
            }
            store.Dispose();

            var backupRoot = Path.Combine(evidenceRoot, "topology-backup-" + runId);
            var backup = await MeasureActivityAsync(() => FilesystemObjectBackup.BackupAsync(
                root, [artifactBucket, diagnosticsBucket], backupRoot, TimeProvider.System, CancellationToken.None)).ConfigureAwait(false);
            Directory.Delete(artifactPath, recursive: true);
            Directory.Delete(diagnosticsPath, recursive: true);
            var restore = await MeasureActivityAsync(() => FilesystemObjectBackup.RestoreAsync(
                backupRoot, root, [artifactBucket, diagnosticsBucket], CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(0, await FilesystemObjectBackup.VerifyAsync(backupRoot, root, CancellationToken.None).ConfigureAwait(false));

            var evidence = new
            {
                Schema = "hvo-issue-586-filesystem-topology-performance-v1",
                Revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "unknown",
                RunId = runId,
                Environment = new
                {
                    OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    Processors = Environment.ProcessorCount,
                    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    Root = root,
                    Drive = DescribeDrive(root)
                },
                Workloads = new
                {
                    W1 = w1,
                    W2 = w2,
                    W4 = w4,
                    W3M = new { Count = metadataCount, List = list.Metrics },
                    W3P = new { Count = backlogCount, PayloadBytes = W2Bytes, Drain = drain.Metrics, DrainedBytes = drain.Result, RestartReconciliation = restart.Metrics, restart.Result.LiveObjects },
                    BackupRestore = new { backup.Result.ObjectCount, backup.Result.TotalBytes, Backup = backup.Metrics, Restore = restore.Metrics }
                }
            };
            Directory.CreateDirectory(evidenceRoot);
            var output = Path.Combine(evidenceRoot, $"topology-performance-{runId}.json");
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(evidence, Json)).ConfigureAwait(false);
            Console.WriteLine($"issue-586 topology evidence: {output}");
            Console.WriteLine($"W1 median={w1.MedianMilliseconds:F1}ms p95={w1.P95Milliseconds:F1}ms ops/s={w1.OperationsPerSecond:F2}");
            Console.WriteLine($"W2 median={w2.MedianMilliseconds:F1}ms p95={w2.P95Milliseconds:F1}ms ops/s={w2.OperationsPerSecond:F2}");
            Console.WriteLine($"W3M list={list.Metrics.ElapsedMilliseconds:F0}ms W3P drain={drain.Metrics.ElapsedMilliseconds:F0}ms restart={restart.Metrics.ElapsedMilliseconds:F0}ms backup={backup.Metrics.ElapsedMilliseconds:F0}ms restore={restore.Metrics.ElapsedMilliseconds:F0}ms");
        }
        finally
        {
            store.Dispose();
            foreach (var path in new[] { artifactPath, diagnosticsPath })
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
        }
    }

    private static FilesystemObjectStore OpenStore(string root, string artifactBucket, string diagnosticsBucket)
    {
        var options = new CentralObjectStorageOptions { Provider = ObjectStorageProvider.Filesystem, ArtifactBucket = artifactBucket, DiagnosticsBucket = diagnosticsBucket };
        options.Filesystem.Root = root;
        var wrapped = Options.Create(options);
        return new FilesystemObjectStore(wrapped, new ObjectStoreTelemetry(wrapped), TimeProvider.System, NullLogger<FilesystemObjectStore>.Instance);
    }

    private static async Task<Measurement> MeasureAsync(string name, IObjectStore store, string bucket, int bytes, int warmups, int operations, int concurrency)
    {
        await Parallel.ForEachAsync(Enumerable.Range(-warmups, warmups), new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            (i, ct) => WorkflowAsync(store, bucket, $"{name}/warm/{i}", bytes, ct)).ConfigureAwait(false);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpu = process.TotalProcessorTime;
        var allocations = GC.GetTotalAllocatedBytes(precise: true);
        var rss = process.WorkingSet64;
        var ioBefore = ProcessIo.Read();
        await using var peak = new PeakWorkingSetSampler(process);
        var latencies = new double[operations];
        var started = Stopwatch.GetTimestamp();
        await Parallel.ForEachAsync(Enumerable.Range(0, operations), new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (i, ct) =>
        {
            var operationStarted = Stopwatch.GetTimestamp();
            await WorkflowAsync(store, bucket, $"{name}/measured/{i:D4}", bytes, ct).ConfigureAwait(false);
            latencies[i] = Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds;
        }).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        Array.Sort(latencies);
        return new(name, operations, concurrency, elapsed.TotalMilliseconds, latencies[operations / 2], latencies[(int)Math.Ceiling(operations * .95) - 1], operations / elapsed.TotalSeconds,
            process.TotalProcessorTime.TotalMilliseconds - cpu.TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - allocations, rss, process.WorkingSet64,
            await peak.StopAsync().ConfigureAwait(false), ProcessIo.Delta(ioBefore, ProcessIo.Read()));
    }

    private static async ValueTask WorkflowAsync(IObjectStore store, string bucket, string prefix, int bytes, CancellationToken cancellationToken)
    {
        var sourceKey = prefix + ".source";
        var destinationKey = prefix + ".copy";
        using var source = new PatternStream(bytes);
        await store.PutAsync(bucket, sourceKey, source, bytes, "application/octet-stream", cancellationToken).ConfigureAwait(false);
        await store.CopyAsync(bucket, sourceKey, destinationKey, cancellationToken).ConfigureAwait(false);
        var destination = await store.StatAsync(bucket, destinationKey, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(bytes, destination.ContentLength);
        await ReadAndVerifyAsync(store, bucket, destinationKey, bytes, cancellationToken).ConfigureAwait(false);
        await store.DeleteAsync(bucket, sourceKey, cancellationToken).ConfigureAwait(false);
        await store.DeleteAsync(bucket, destinationKey, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SeedAsync(IObjectStore store, string bucket, string prefix, int count, int bytes, int concurrency)
        => await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (i, ct) =>
        {
            using var source = new PatternStream(bytes);
            await store.PutAsync(bucket, $"{prefix}{i:D5}", source, bytes, "application/octet-stream", ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

    private static string DescribeDrive(string root)
    {
        var drive = new DriveInfo(root);
        return $"{drive.DriveFormat} total={drive.TotalSize} free={drive.AvailableFreeSpace}";
    }

    private static async Task<long> ReadAndVerifyAsync(IObjectStore store, string bucket, string key, int expectedBytes, CancellationToken cancellationToken)
    {
        long length = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await store.ReadAsync(bucket, key, null, async (stream, ct) =>
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                length += read;
            }
        }, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(expectedBytes, length);
        Assert.AreEqual(PatternSha256(expectedBytes), Convert.ToHexStringLower(hash.GetHashAndReset()));
        return length;
    }

    private static string PatternSha256(int bytes)
        => bytes switch
        {
            W1Bytes => W1Sha256,
            W2Bytes => W2Sha256,
            _ => CreatePatternSha256(bytes)
        };

    private static string CreatePatternSha256(int bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Math.Min(bytes, 64 * 1024)];
        buffer.AsSpan().Fill(0x5a);
        for (var remaining = bytes; remaining > 0; remaining -= buffer.Length)
        {
            hash.AppendData(buffer, 0, Math.Min(buffer.Length, remaining));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task<ActivityMeasurement<T>> MeasureActivityAsync<T>(Func<Task<T>> activity)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpu = process.TotalProcessorTime;
        var allocations = GC.GetTotalAllocatedBytes(precise: true);
        var rss = process.WorkingSet64;
        var ioBefore = ProcessIo.Read();
        await using var peak = new PeakWorkingSetSampler(process);
        var started = Stopwatch.GetTimestamp();
        var result = await activity().ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        process.Refresh();
        return new(result, new ActivityMetrics(elapsed, process.TotalProcessorTime.TotalMilliseconds - cpu.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(true) - allocations, rss, process.WorkingSet64, await peak.StopAsync().ConfigureAwait(false),
            ProcessIo.Delta(ioBefore, ProcessIo.Read())));
    }

    private sealed record Measurement(string Name, int Operations, int Concurrency, double ElapsedMilliseconds, double MedianMilliseconds,
        double P95Milliseconds, double OperationsPerSecond, double CpuMilliseconds, long AllocatedBytes, long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes, long PeakWorkingSetBytes, ProcessIo Io);

    private sealed record ActivityMeasurement<T>(T Result, ActivityMetrics Metrics);

    private sealed record ActivityMetrics(double ElapsedMilliseconds, double CpuMilliseconds, long AllocatedBytes,
        long WorkingSetBeforeBytes, long WorkingSetAfterBytes, long PeakWorkingSetBytes, ProcessIo Io);

    private sealed class PeakWorkingSetSampler : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _sampling;
        private long _peak;

        public PeakWorkingSetSampler(Process process)
        {
            _process = process;
            _peak = process.WorkingSet64;
            _sampling = SampleAsync();
        }

        public async Task<long> StopAsync()
        {
            if (!_stop.IsCancellationRequested) await _stop.CancelAsync().ConfigureAwait(false);
            await _sampling.ConfigureAwait(false);
            return _peak;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _stop.Dispose();
        }

        private async Task SampleAsync()
        {
            try
            {
                while (true)
                {
                    _process.Refresh();
                    _peak = Math.Max(_peak, _process.WorkingSet64);
                    await Task.Delay(10, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                _process.Refresh();
                _peak = Math.Max(_peak, _process.WorkingSet64);
            }
        }
    }

    private sealed record ProcessIo(long ReadChars, long WriteChars, long ReadSyscalls, long WriteSyscalls, long ReadBytes, long WriteBytes)
    {
        public static ProcessIo Read()
        {
            if (!OperatingSystem.IsLinux()) return new(0, 0, 0, 0, 0, 0);
            var values = File.ReadLines("/proc/self/io").Select(line => line.Split(':', 2)).ToDictionary(parts => parts[0], parts => long.Parse(parts[1], CultureInfo.InvariantCulture));
            return new(values["rchar"], values["wchar"], values["syscr"], values["syscw"], values["read_bytes"], values["write_bytes"]);
        }
        public static ProcessIo Delta(ProcessIo before, ProcessIo after) => new(after.ReadChars - before.ReadChars, after.WriteChars - before.WriteChars,
            after.ReadSyscalls - before.ReadSyscalls, after.WriteSyscalls - before.WriteSyscalls, after.ReadBytes - before.ReadBytes, after.WriteBytes - before.WriteBytes);
    }

    private sealed class PatternStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - _position);
            if (read <= 0) return 0;
            buffer.AsSpan(offset, read).Fill(0x5a); _position += read; return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, length - _position);
            if (read <= 0) return ValueTask.FromResult(0);
            buffer.Span[..read].Fill(0x5a); _position += read; return ValueTask.FromResult(read);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
