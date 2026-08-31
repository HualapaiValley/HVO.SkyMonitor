using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.DependencyInjection;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed partial class S3ObjectStorePerformanceTests
{
    private const string Bucket = "skymonitor-artifacts";
    private const string ContentType = "application/x-skymonitor-100mib";
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NonSeekable100MiBUpload_RecordsBoundedRegressionEvidence()
    {
        var runId = Guid.NewGuid().ToString("N");
        var baselineKey = $"issue-504/performance/{runId}/baseline.bin";
        var candidateKey = $"issue-504/performance/{runId}/candidate.bin";
        var fixtureClient = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        if (!await fixtureClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket)).ConfigureAwait(false))
        {
            await fixtureClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket)).ConfigureAwait(false);
        }

        using var handler = new CountingHandler { InnerHandler = new SocketsHttpHandler() };
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new MinioClient()
            .WithEndpoint(AssemblyHooks.Fixture.MinioEndpoint)
            .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
            .WithHttpClient(httpClient, disposeHttpClient: false)
            .Build();
        var options = CreateS3Options();
        using var candidateClient = S3ObjectStoreClientFactory.Create(
            options,
            new ObjectStoreTestClient.SharedHttpClientFactory(httpClient));
        var store = ObjectStoreTestClient.Create(candidateClient, options);
        try
        {
            var baseline = await MeasureUploadAsync(
                handler,
                async content =>
                {
                    await client.PutObjectAsync(new PutObjectArgs()
                        .WithBucket(Bucket)
                        .WithObject(baselineKey)
                        .WithContentType(ContentType)
                        .WithStreamData(content)
                        .WithObjectSize(IObjectStore.MaximumPutContentLength)).ConfigureAwait(false);
                }).ConfigureAwait(false);
            var candidate = await MeasureUploadAsync(
                handler,
                content => store.PutAsync(
                    Bucket,
                    candidateKey,
                    content,
                    IObjectStore.MaximumPutContentLength,
                    ContentType,
                    CancellationToken.None)).ConfigureAwait(false);

            Assert.AreEqual(IObjectStore.MaximumPutContentLength, baseline.BytesRead);
            Assert.AreEqual(IObjectStore.MaximumPutContentLength, candidate.BytesRead);
            Assert.IsFalse(baseline.SourceDisposed);
            Assert.IsFalse(candidate.SourceDisposed);
            Assert.IsFalse(baseline.SeekAttempted);
            Assert.IsFalse(candidate.SeekAttempted);
            CollectionAssert.AreEqual(baseline.Sha256, candidate.Sha256);
            Assert.IsTrue(candidate.RequestCount <= baseline.RequestCount + 1);
            Assert.IsTrue(candidate.ElapsedMilliseconds <= baseline.ElapsedMilliseconds * 2 + 2000);
            Assert.IsTrue(candidate.AllocatedBytes <= baseline.AllocatedBytes * 3 / 2 + 16 * 1024 * 1024);
            Assert.IsTrue(candidate.WorkingSetDeltaBytes <= 128L * 1024 * 1024);
            Assert.IsTrue(candidate.ThroughputMiBPerSecond >= 1);

            var stat = await store.StatAsync(Bucket, candidateKey, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(IObjectStore.MaximumPutContentLength, stat.ContentLength);
            Assert.AreEqual(ContentType, stat.ContentType);
            using var readHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var readBytes = 0L;
            await store.ReadAsync(Bucket, candidateKey, stat.Generation, async (content, cancellationToken) =>
            {
                var buffer = GC.AllocateUninitializedArray<byte>(1024 * 1024);
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    readHash.AppendData(buffer, 0, read);
                    readBytes += read;
                }
            }, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(IObjectStore.MaximumPutContentLength, readBytes);
            CollectionAssert.AreEqual(candidate.Sha256, readHash.GetHashAndReset());

            var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "local-uncommitted";
            var evidence = new
            {
                Schema = "hvo-issue-504-object-store-performance-v1",
                Revision = revision,
                RunId = runId,
                ObjectBytes = IObjectStore.MaximumPutContentLength,
                Baseline = baseline,
                Candidate = candidate,
                ReadBytes = readBytes,
                Sha256 = Convert.ToHexString(candidate.Sha256),
                Integrity = "exact-length-and-sha256-pass",
                Bounds = "forward-only-source; candidate allocations <= baseline*1.5+16MiB; RSS delta <=128MiB"
            };
            var outputDirectory = Path.Combine(
                FindRepositoryRoot(),
                "TestResults",
                "issue-504",
                revision,
                runId);
            Directory.CreateDirectory(outputDirectory);
            var evidencePath = Path.Combine(outputDirectory, "s3-object-store-performance.json");
            await File.WriteAllTextAsync(
                evidencePath,
                JsonSerializer.Serialize(evidence, EvidenceJsonOptions))
                .ConfigureAwait(false);
            TestContext.AddResultFile(evidencePath);
            TestContext.WriteLine(
                "Issue #504 baseline {0:F1}ms/{1} requests; candidate {2:F1}ms/{3} requests; evidence {4}",
                baseline.ElapsedMilliseconds,
                baseline.RequestCount,
                candidate.ElapsedMilliseconds,
                candidate.RequestCount,
                evidencePath);
        }
        finally
        {
            await client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(baselineKey))
                .ConfigureAwait(false);
            await client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(candidateKey))
                .ConfigureAwait(false);
        }
    }

    private static CentralObjectStorageOptions CreateS3Options()
        => new()
        {
            ServiceEndpoint = AssemblyHooks.Fixture.MinioEndpoint,
            Region = "us-east-1",
            UseTls = false,
            AddressingStyle = ObjectStorageAddressingStyle.Path,
            CredentialMode = ObjectStorageCredentialMode.Static,
            AccessKey = IntegrationTestFixture.MinioAccessKey,
            SecretKey = IntegrationTestFixture.MinioSecretKey
        };

    private static async Task<UploadMeasurement> MeasureUploadAsync(
        CountingHandler handler,
        Func<Stream, Task> upload)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var workingSetBefore = process.WorkingSet64;
        handler.Reset();
        using var source = new PatternReadStream(IObjectStore.MaximumPutContentLength);
        var started = Stopwatch.GetTimestamp();
        await upload(source).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var measurement = new UploadMeasurement(
            elapsed.TotalMilliseconds,
            IObjectStore.MaximumPutContentLength / 1024d / 1024d / elapsed.TotalSeconds,
            process.TotalProcessorTime.TotalMilliseconds - cpuBefore.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore,
            workingSetBefore,
            process.WorkingSet64,
            process.WorkingSet64 - workingSetBefore,
            handler.RequestCount,
            source.BytesRead,
            source.SeekAttempted,
            source.WasDisposed,
            source.GetSha256());
        return measurement;
    }

    private sealed record UploadMeasurement(
        double ElapsedMilliseconds,
        double ThroughputMiBPerSecond,
        double CpuMilliseconds,
        long AllocatedBytes,
        long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes,
        long WorkingSetDeltaBytes,
        int RequestCount,
        long BytesRead,
        bool SeekAttempted,
        bool SourceDisposed,
        byte[] Sha256);

    private sealed class CountingHandler : DelegatingHandler
    {
        private int _requestCount;
        private long _requestBytes;
        private long _responseBytes;
        private int _gets;
        private int _heads;
        private int _puts;
        private int _posts;
        private int _deletes;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public void Reset()
        {
            Interlocked.Exchange(ref _requestCount, 0);
            Interlocked.Exchange(ref _requestBytes, 0);
            Interlocked.Exchange(ref _responseBytes, 0);
            Interlocked.Exchange(ref _gets, 0);
            Interlocked.Exchange(ref _heads, 0);
            Interlocked.Exchange(ref _puts, 0);
            Interlocked.Exchange(ref _posts, 0);
            Interlocked.Exchange(ref _deletes, 0);
        }

        public ProtocolMeasurement Snapshot() => new(
            RequestCount,
            Interlocked.Read(ref _requestBytes),
            Interlocked.Read(ref _responseBytes),
            Volatile.Read(ref _gets),
            Volatile.Read(ref _heads),
            Volatile.Read(ref _puts),
            Volatile.Read(ref _posts),
            Volatile.Read(ref _deletes));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (request.Content?.Headers.ContentLength is { } requestLength)
            {
                Interlocked.Add(ref _requestBytes, requestLength);
            }
            if (request.Method == HttpMethod.Get) Interlocked.Increment(ref _gets);
            else if (request.Method == HttpMethod.Head) Interlocked.Increment(ref _heads);
            else if (request.Method == HttpMethod.Put) Interlocked.Increment(ref _puts);
            else if (request.Method == HttpMethod.Post) Interlocked.Increment(ref _posts);
            else if (request.Method == HttpMethod.Delete) Interlocked.Increment(ref _deletes);
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.Method == HttpMethod.Get && response.Content.Headers.ContentLength is { } responseLength)
            {
                Interlocked.Add(ref _responseBytes, responseLength);
            }
            return response;
        }
    }

    private sealed record ProtocolMeasurement(
        int Requests,
        long RequestContentLengthBytes,
        long GetResponseContentLengthBytes,
        int Gets,
        int Heads,
        int Puts,
        int Posts,
        int Deletes);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class PatternReadStream(long length) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _position;
        private byte[]? _sha256;

        public long BytesRead => _position;
        public bool SeekAttempted { get; private set; }
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - _position);
            for (var index = 0; index < count; index++)
            {
                buffer[index] = (byte)(((_position + index) * 31 + 17) % 251);
            }
            if (count > 0)
            {
                _hash.AppendData(buffer[..count]);
                _position += count;
            }
            return count;
        }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public byte[] GetSha256() => _sha256 ??= _hash.GetHashAndReset();
        public override long Seek(long offset, SeekOrigin origin)
        {
            SeekAttempted = true;
            throw new NotSupportedException();
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            if (disposing)
            {
                _hash.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
