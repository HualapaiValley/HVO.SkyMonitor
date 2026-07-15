using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using HVO.SkyMonitor.TestSupport;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.DependencyInjection;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class LogicHostRetrievalPerformanceTests
{
    private const int StandardWarmups = 5;
    private const int StandardMeasurements = 30;
    private const int ConcurrentWarmups = 20;
    private const int ConcurrentMeasurements = 200;
    private static readonly int[] ConcurrencyLevels = [1, 4, 8];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [TestMethod]
    public async Task AuthorizedRetrieval_W1W2AndW4_RecordsPerformanceEvidence()
    {
        var fixture = AssemblyHooks.Fixture;
        var w1Payload = CreatePayload(4_708_352, 17);
        var w2Payload = CreatePayload(12_879_360, 29);
        var w1 = await ArtifactRetrievalTests.SeedArtifactAsync(TestUsers.Operator.Email, w1Payload).ConfigureAwait(false);
        var w2 = await ArtifactRetrievalTests.SeedArtifactAsync(TestUsers.Operator.Email, w2Payload).ConfigureAwait(false);
        var missingArtifacts = new List<ArtifactRetrievalTests.SeededArtifact>();
        var corruptArtifacts = new List<ArtifactRetrievalTests.SeededArtifact>();
        for (var index = 0; index < StandardWarmups + StandardMeasurements; index++)
        {
            missingArtifacts.Add(await ArtifactRetrievalTests.SeedArtifactAsync(
                TestUsers.Operator.Email, [1, 2, 3, 4], putObject: false).ConfigureAwait(false));
            corruptArtifacts.Add(await ArtifactRetrievalTests.SeedArtifactAsync(
                TestUsers.Operator.Email, [1, 2, 3, 4], persistedChecksum: new string('A', 64)).ConfigureAwait(false));
        }
        using var client = await ArtifactRetrievalTests.CreateUserClientAsync(
            TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();

        var direct = new[]
        {
            await MeasureDirectAsync(minio, "direct-W1", w1, w1Payload, StandardWarmups, StandardMeasurements).ConfigureAwait(false),
            await MeasureDirectAsync(minio, "direct-W2", w2, w2Payload, StandardWarmups, StandardMeasurements).ConfigureAwait(false)
        };
        var telemetryMeasurements = new ConcurrentBag<TelemetryMeasurement>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CentralArtifactRetrievalTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            telemetryMeasurements.Add(new TelemetryMeasurement(
                instrument.Name,
                value,
                tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value?.ToString(), StringComparer.Ordinal))));
        meterListener.Start();
        var candidate = new List<RetrievalMeasurement>
        {
            await MeasureHttpAsync(client, "full-W1", w1, w1Payload, null, StandardWarmups, StandardMeasurements, 1)
                .ConfigureAwait(false),
            await MeasureHttpAsync(client, "full-W2", w2, w2Payload, null, StandardWarmups, StandardMeasurements, 1)
                .ConfigureAwait(false)
        };
        foreach (var concurrency in ConcurrencyLevels)
        {
            candidate.Add(await MeasureHttpAsync(
                client,
                $"W4-W1-1MiB-C{concurrency}",
                w1,
                w1Payload,
                new RangeHeaderValue(1_048_576, 2_097_151),
                ConcurrentWarmups,
                ConcurrentMeasurements,
                concurrency).ConfigureAwait(false));
        }
        candidate.Add(await MeasureDeniedAsync(w1.ContentUri).ConfigureAwait(false));
        candidate.Add(await MeasureFailureAsync(
            client, "missing-object", missingArtifacts, System.Net.HttpStatusCode.ServiceUnavailable).ConfigureAwait(false));
        candidate.Add(await MeasureFailureAsync(
            client, "checksum-mismatch", corruptArtifacts, System.Net.HttpStatusCode.Conflict).ConfigureAwait(false));

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var evidence = new
        {
            Issue = 99,
            BaselineRevision = "08ddd7976be75422b88d0899deb99427993ee827",
            CandidateRevision = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "working-tree",
            Workloads = new
            {
                W1 = new { Bytes = w1Payload.LongLength, Sha256 = w1.Checksum },
                W2 = new { Bytes = w2Payload.LongLength, Sha256 = w2.Checksum },
                W4 = new { Warmups = ConcurrentWarmups, Measurements = ConcurrentMeasurements, ConcurrencyLevels }
            },
            Method = new
            {
                Baseline = "Direct authenticated MinIO full reads are non-equivalent storage-only evidence because baseline main has no authorized retrieval endpoint.",
                Candidate = "LogicHost owner authorization, SQL lookup, full SHA-256 verification, conditional bounded streaming, and exact response validation.",
                Range = "MinIO 7 rejects ranged GetObject helper stat calls with PartialContentException, so the bounded callback discards the prefix and stops after the requested bytes. This is explicit measured I/O, not whole-object buffering.",
                Latency = "Nearest-rank median/p95 over 30 operations after five warmups; W4 uses 200 operations after 20 warmups.",
                Resources = "GC.GetTotalAllocatedBytes and Process CPU/working-set observations cover the in-process host and test client; SQL Server and MinIO container CPU/RSS are excluded.",
                ObjectIo = "Each candidate read performs full verification plus one conditional serving GET. Denied requests return before object access. Application response and verified byte counts are exact; transport framing and unavailable SQL wire bytes are not estimated."
            },
            DirectStorageBaseline = direct,
            Candidate = candidate,
            ObservedRetrievalTelemetry = telemetryMeasurements
                .GroupBy(measurement => new
                {
                    measurement.Name,
                    Operation = measurement.Tags.GetValueOrDefault("operation"),
                    Outcome = measurement.Tags.GetValueOrDefault("outcome")
                })
                .Select(group => new
                {
                    group.Key.Name,
                    group.Key.Operation,
                    group.Key.Outcome,
                    Value = group.Sum(measurement => measurement.Value)
                })
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Operation, StringComparer.Ordinal)
                .ThenBy(item => item.Outcome, StringComparer.Ordinal)
                .ToArray(),
            Process = new
            {
                process.TotalProcessorTime.TotalMilliseconds,
                process.WorkingSet64,
                process.PeakWorkingSet64,
                AllocatedBytes = GC.GetTotalAllocatedBytes(false)
            },
            Environment = new
            {
                Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorCount = Environment.ProcessorCount
            },
            Correctness = new
            {
                ExactFullBytes = true,
                ExactRangeBytes = true,
                ChecksumAndEtag = true,
                UnauthorizedObjectReads = 0,
                WholeObjectResponseBuffering = false
            },
            RecordedAtUtc = DateTimeOffset.UtcNow
        };
        var outputDirectory = Path.Combine(GetRepositoryRoot(), "TestResults", "issue-99", evidence.CandidateRevision);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "logichost-retrieval-performance.json"),
            JsonSerializer.Serialize(evidence, JsonOptions)).ConfigureAwait(false);
    }

    private static async Task<RetrievalMeasurement> MeasureDirectAsync(
        IMinioClient minio,
        string scenario,
        ArtifactRetrievalTests.SeededArtifact artifact,
        byte[] expected,
        int warmups,
        int measurements)
    {
        for (var index = 0; index < warmups; index++)
        {
            await ReadDirectAsync(minio, artifact.ObjectKey, expected).ConfigureAwait(false);
        }
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        process.Refresh();
        var rssBefore = process.WorkingSet64;
        var durations = new double[measurements];
        for (var index = 0; index < measurements; index++)
        {
            var started = Stopwatch.GetTimestamp();
            await ReadDirectAsync(minio, artifact.ObjectKey, expected).ConfigureAwait(false);
            durations[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        process.Refresh();
        return CreateMeasurement(
            scenario,
            durations,
            expected.LongLength,
            measurements,
            1,
            GC.GetTotalAllocatedBytes(false) - allocatedBefore,
            (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            process.WorkingSet64 - rssBefore);
    }

    private static async Task ReadDirectAsync(IMinioClient minio, string objectKey, byte[] expected)
    {
        await using var destination = new MemoryStream(expected.Length);
        await minio.GetObjectAsync(new GetObjectArgs()
            .WithBucket("skymonitor-artifacts")
            .WithObject(objectKey)
            .WithCallbackStream((stream, cancellationToken) => stream.CopyToAsync(destination, cancellationToken)))
            .ConfigureAwait(false);
        Assert.IsTrue(destination.GetBuffer().AsSpan(0, checked((int)destination.Length)).SequenceEqual(expected));
    }

    private static async Task<RetrievalMeasurement> MeasureHttpAsync(
        HttpClient client,
        string scenario,
        ArtifactRetrievalTests.SeededArtifact artifact,
        byte[] expected,
        RangeHeaderValue? range,
        int warmups,
        int measurements,
        int concurrency)
    {
        await ExecuteHttpAsync(client, artifact, expected, range, warmups, concurrency, null, null).ConfigureAwait(false);
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        process.Refresh();
        var rssBefore = process.WorkingSet64;
        var durations = new double[measurements];
        var timeToFirstByte = new double[measurements];
        var wallStarted = Stopwatch.GetTimestamp();
        await ExecuteHttpAsync(client, artifact, expected, range, measurements, concurrency, durations, timeToFirstByte)
            .ConfigureAwait(false);
        var wallElapsed = Stopwatch.GetElapsedTime(wallStarted);
        process.Refresh();
        var responseBytes = range is null ? expected.LongLength : range.Ranges.Single().To!.Value - range.Ranges.Single().From!.Value + 1;
        return CreateMeasurement(
            scenario,
            durations,
            responseBytes,
            measurements,
            concurrency,
            GC.GetTotalAllocatedBytes(false) - allocatedBefore,
            (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            process.WorkingSet64 - rssBefore,
            wallElapsed.TotalSeconds,
            timeToFirstByte);
    }

    private static async Task ExecuteHttpAsync(
        HttpClient client,
        ArtifactRetrievalTests.SeededArtifact artifact,
        byte[] expected,
        RangeHeaderValue? range,
        int count,
        int concurrency,
        double[]? durations,
        double[]? timeToFirstByte)
    {
        for (var offset = 0; offset < count; offset += concurrency)
        {
            var batch = Enumerable.Range(offset, Math.Min(concurrency, count - offset)).Select(async index =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, artifact.ContentUri);
                request.Headers.Range = range;
                var started = Stopwatch.GetTimestamp();
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var start = range?.Ranges.Single().From ?? 0;
                var end = range?.Ranges.Single().To ?? expected.LongLength - 1;
                await using var validator = new ExpectedBytesWriteStream(expected, checked((int)start), checked((int)(end - start + 1)));
                await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var firstByte = new byte[1];
                Assert.AreEqual(1, await responseStream.ReadAsync(firstByte).ConfigureAwait(false));
                if (timeToFirstByte is not null)
                {
                    timeToFirstByte[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                await validator.WriteAsync(firstByte).ConfigureAwait(false);
                await responseStream.CopyToAsync(validator).ConfigureAwait(false);
                validator.EnsureComplete();
                Assert.AreEqual(artifact.Checksum, response.Headers.GetValues("X-Artifact-SHA256").Single());
                if (durations is not null)
                {
                    durations[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
            });
            await Task.WhenAll(batch).ConfigureAwait(false);
        }
    }

    private static async Task<RetrievalMeasurement> MeasureDeniedAsync(Uri contentUri)
    {
        using var anonymous = AssemblyHooks.Fixture.Factory.CreateClient();
        var durations = new double[StandardMeasurements];
        for (var index = 0; index < StandardMeasurements; index++)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await anonymous.GetAsync(contentUri).ConfigureAwait(false);
            Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
            durations[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        return CreateMeasurement("unauthorized", durations, 0, StandardMeasurements, 1, 0, 0, 0);
    }

    private static async Task<RetrievalMeasurement> MeasureFailureAsync(
        HttpClient client,
        string scenario,
        List<ArtifactRetrievalTests.SeededArtifact> artifacts,
        System.Net.HttpStatusCode expectedStatus)
    {
        for (var index = 0; index < StandardWarmups; index++)
        {
            using var warmup = await client.GetAsync(artifacts[index].ContentUri).ConfigureAwait(false);
            Assert.AreEqual(expectedStatus, warmup.StatusCode);
        }
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        process.Refresh();
        var rssBefore = process.WorkingSet64;
        var durations = new double[StandardMeasurements];
        for (var index = 0; index < StandardMeasurements; index++)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await client.GetAsync(artifacts[index + StandardWarmups].ContentUri).ConfigureAwait(false);
            Assert.AreEqual(expectedStatus, response.StatusCode);
            durations[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        process.Refresh();
        return CreateMeasurement(
            scenario,
            durations,
            0,
            StandardMeasurements,
            1,
            GC.GetTotalAllocatedBytes(false) - allocatedBefore,
            (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            process.WorkingSet64 - rssBefore);
    }

    private static RetrievalMeasurement CreateMeasurement(
        string scenario,
        double[] durations,
        long responseBytes,
        int operations,
        int concurrency,
        long allocatedBytes,
        double cpuMilliseconds,
        long workingSetDeltaBytes,
        double? wallSeconds = null,
        double[]? timeToFirstByte = null)
    {
        Array.Sort(durations);
        var totalSeconds = wallSeconds ?? durations.Sum() / 1000;
        if (timeToFirstByte is not null)
        {
            Array.Sort(timeToFirstByte);
        }
        return new RetrievalMeasurement(
            scenario,
            operations,
            concurrency,
            responseBytes,
            durations[(durations.Length - 1) / 2],
            durations[(int)Math.Ceiling(durations.Length * 0.95) - 1],
            durations[^1],
            timeToFirstByte?[(timeToFirstByte.Length - 1) / 2],
            timeToFirstByte?[(int)Math.Ceiling(timeToFirstByte.Length * 0.95) - 1],
            totalSeconds <= 0 ? 0 : responseBytes * operations / totalSeconds,
            allocatedBytes,
            cpuMilliseconds,
            workingSetDeltaBytes);
    }

    private static byte[] CreatePayload(int length, int seed)
    {
        var payload = new byte[length];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)((index * 31L + seed) % 251);
        }
        return payload;
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed record RetrievalMeasurement(
        string Scenario,
        int Operations,
        int Concurrency,
        long ResponseBytesPerOperation,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds,
        double? MedianTimeToFirstByteMilliseconds,
        double? P95TimeToFirstByteMilliseconds,
        double ResponseBytesPerSecond,
        long AllocatedBytes,
        double CpuMilliseconds,
        long WorkingSetDeltaBytes);

    private sealed record TelemetryMeasurement(
        string Name,
        long Value,
        IReadOnlyDictionary<string, string?> Tags);

    private sealed class ExpectedBytesWriteStream(byte[] expected, int offset, int length) : Stream
    {
        private int _position;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public void EnsureComplete() => Assert.AreEqual(length, _position);

        public override void Write(byte[] buffer, int bufferOffset, int count)
        {
            Validate(buffer.AsSpan(bufferOffset, count));
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int bufferOffset, int count) => throw new NotSupportedException();
        public override long Seek(long seekOffset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void Validate(ReadOnlySpan<byte> actual)
        {
            if (_position + actual.Length > length
                || !actual.SequenceEqual(expected.AsSpan(offset + _position, actual.Length)))
            {
                Assert.Fail("Retrieved bytes differ from the expected artifact range.");
            }
            _position += actual.Length;
        }
    }
}
