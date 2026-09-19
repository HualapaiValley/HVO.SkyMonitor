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
            var listStarted = Stopwatch.GetTimestamp();
            var listed = 0;
            await foreach (var _ in store.ListAsync(artifactBucket, "w3m/", CancellationToken.None)) listed++;
            var listMilliseconds = Stopwatch.GetElapsedTime(listStarted).TotalMilliseconds;
            Assert.AreEqual(metadataCount, listed);

            const int backlogCount = 100;
            await SeedAsync(store, artifactBucket, "w3p/", backlogCount, W2Bytes, 8).ConfigureAwait(false);
            store.Dispose();
            var restartStarted = Stopwatch.GetTimestamp();
            store = OpenStore(root, artifactBucket, diagnosticsBucket);
            var reconciliation = new FilesystemObjectReconciler(store, TimeProvider.System, NullLogger<FilesystemObjectReconciler>.Instance)
                .Reconcile(artifactBucket, new FilesystemReconciliationOptions(), CancellationToken.None);
            var restartMilliseconds = Stopwatch.GetElapsedTime(restartStarted).TotalMilliseconds;
            var drainStarted = Stopwatch.GetTimestamp();
            long drainedBytes = 0;
            await Parallel.ForEachAsync(Enumerable.Range(0, backlogCount), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (i, ct) =>
            {
                var key = $"w3p/{i:D5}";
                var stat = await store.StatAsync(artifactBucket, key, ct).ConfigureAwait(false);
                Interlocked.Add(ref drainedBytes, stat.ContentLength);
                await store.DeleteAsync(artifactBucket, key, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
            var drainMilliseconds = Stopwatch.GetElapsedTime(drainStarted).TotalMilliseconds;

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
            var backupStarted = Stopwatch.GetTimestamp();
            var inventory = await FilesystemObjectBackup.BackupAsync(root, [artifactBucket, diagnosticsBucket], backupRoot, TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
            var backupMilliseconds = Stopwatch.GetElapsedTime(backupStarted).TotalMilliseconds;
            Directory.Delete(artifactPath, recursive: true);
            Directory.Delete(diagnosticsPath, recursive: true);
            var restoreStarted = Stopwatch.GetTimestamp();
            await FilesystemObjectBackup.RestoreAsync(backupRoot, root, [artifactBucket, diagnosticsBucket], CancellationToken.None).ConfigureAwait(false);
            var restoreMilliseconds = Stopwatch.GetElapsedTime(restoreStarted).TotalMilliseconds;
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
                    W3M = new { Count = metadataCount, ListMilliseconds = listMilliseconds },
                    W3P = new { Count = backlogCount, PayloadBytes = W2Bytes, DrainMilliseconds = drainMilliseconds, DrainedBytes = drainedBytes, RestartReconciliationMilliseconds = restartMilliseconds, reconciliation.LiveObjects },
                    BackupRestore = new { inventory.ObjectCount, inventory.TotalBytes, BackupMilliseconds = backupMilliseconds, RestoreMilliseconds = restoreMilliseconds }
                }
            };
            Directory.CreateDirectory(evidenceRoot);
            var output = Path.Combine(evidenceRoot, $"topology-performance-{runId}.json");
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(evidence, Json)).ConfigureAwait(false);
            Console.WriteLine($"issue-586 topology evidence: {output}");
            Console.WriteLine($"W1 median={w1.MedianMilliseconds:F1}ms p95={w1.P95Milliseconds:F1}ms ops/s={w1.OperationsPerSecond:F2}");
            Console.WriteLine($"W2 median={w2.MedianMilliseconds:F1}ms p95={w2.P95Milliseconds:F1}ms ops/s={w2.OperationsPerSecond:F2}");
            Console.WriteLine($"W3M list={listMilliseconds:F0}ms W3P drain={drainMilliseconds:F0}ms restart={restartMilliseconds:F0}ms backup={backupMilliseconds:F0}ms restore={restoreMilliseconds:F0}ms");
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
            process.TotalProcessorTime.TotalMilliseconds - cpu.TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - allocations, rss, process.WorkingSet64, ProcessIo.Delta(ioBefore, ProcessIo.Read()));
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

    private sealed record Measurement(string Name, int Operations, int Concurrency, double ElapsedMilliseconds, double MedianMilliseconds,
        double P95Milliseconds, double OperationsPerSecond, double CpuMilliseconds, long AllocatedBytes, long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes, ProcessIo Io);

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
