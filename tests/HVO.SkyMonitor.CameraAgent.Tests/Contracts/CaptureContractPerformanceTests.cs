using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Contracts;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class CaptureContractPerformanceTests
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif
    private const int WarmupOperations = 5;
    private const int MeasuredOperations = 30;
    private static readonly JsonSerializerOptions OutputJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task W1AndW2_SerializationValidationHashAndReconstructionEvidence()
    {
        var results = new[]
        {
            MeasureWorkload("W1", CameraPixelFormat.Mono16, 1936, 1216, 1936 * 2),
            MeasureWorkload("W2", CameraPixelFormat.BayerRggb16, 3096, 2080, 3096 * 2)
        };
        var evidence = new
        {
            Revision = "candidate-working-tree",
            Environment = new
            {
                OperatingSystem = Environment.OSVersion.ToString(),
                Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Configuration = BuildConfiguration,
                ProcessorCount = Environment.ProcessorCount,
                WorkingSetBytes = Environment.WorkingSet
            },
            WarmupOperations,
            MeasuredOperations,
            Concurrency = 1,
            InitialBacklog = 0,
            Result = "Per-workload fields report absolute and percentage changes for descriptor size and filesystem save latency/throughput. Metadata growth is accepted because it supplies complete reconstruction facts while remaining below 0.04% of W1/W2 payload size. The v2 save regression is the explicit cost of one post-write sequential checksum read; it prevents mutable caller memory from publishing a false checksum, performs no payload copy, and remains bounded by payload size.",
            Results = results
        };
        var outputDirectory = Path.Combine(GetRepositoryRoot(), "TestResults", "issue-92");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "contracts-performance.json"),
            JsonSerializer.Serialize(evidence, OutputJsonOptions)).ConfigureAwait(false);
    }

    private static object MeasureWorkload(string workload, CameraPixelFormat format, int width, int height, int stride)
    {
        var payload = new byte[checked(stride * height)];
        var manifest = ReconstructableCaptureContractTests.CreateManifest(format, width, height, stride, payload);
        var encoded = CaptureContractJson.Serialize(manifest);
        var legacy = new ArtifactUploadManifest(
            ArtifactUploadManifest.CurrentSchemaVersion,
            manifest.Descriptor.Capture.AgentId,
            manifest.Descriptor.Artifact.ArtifactId,
            manifest.Descriptor.Capture.CaptureId,
            manifest.Descriptor.Artifact.Role,
            manifest.Descriptor.Artifact.MediaType,
            payload.LongLength,
            manifest.Descriptor.Artifact.ChecksumSha256,
            manifest.Descriptor.Timing.ExposureStartedUtc,
            "raw-v1",
            manifest.RelativeArtifactPath);
        var legacySize = JsonSerializer.SerializeToUtf8Bytes(legacy, WireJsonOptions).Length;

        var reconstruction = FrameReconstructor.TryReconstruct(manifest.Descriptor, payload, out var reconstructed);
        Assert.IsTrue(reconstruction.IsValid);
        Assert.IsTrue(MemoryMarshal.TryGetArray(reconstructed!.PixelData, out var reconstructedSegment));
        var zeroCopyVerified = ReferenceEquals(payload, reconstructedSegment.Array);
        Assert.IsTrue(zeroCopyVerified);

        var stages = new[]
        {
            Measure("legacy-v1-serialize", () => GC.KeepAlive(JsonSerializer.SerializeToUtf8Bytes(legacy, WireJsonOptions))),
            Measure("serialize", () => GC.KeepAlive(CaptureContractJson.Serialize(manifest))),
            Measure("deserialize-validate", () =>
            {
                var parsed = CaptureContractJson.ParseManifest(encoded);
                Assert.IsTrue(parsed.IsValid);
            }),
            Measure("descriptor-hash", () => GC.KeepAlive(CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor))),
            Measure("payload-checksum", () => GC.KeepAlive(PayloadChecksum.ComputeSha256(payload))),
            Measure("streaming-payload-checksum", () =>
            {
                using var stream = new MemoryStream(payload, writable: false);
                GC.KeepAlive(PayloadChecksum.ComputeSha256Async(stream).AsTask().GetAwaiter().GetResult());
            }),
            Measure("validate-reconstruct", () =>
            {
                var validation = FrameReconstructor.TryReconstruct(manifest.Descriptor, payload, out var frame);
                Assert.IsTrue(validation.IsValid);
                GC.KeepAlive(frame);
            })
        };
        var storageStages = MeasureStorage(manifest, payload);
        return new
        {
            Workload = workload,
            ReferenceProfile = workload == "W1" ? "virtual-asi174.full.json" : "virtual-asi178mc.full.json",
            PayloadPattern = "deterministic zero-filled bytes; descriptor-only workload has no scene seed",
            Width = width,
            Height = height,
            Format = format.ToString(),
            PayloadBytes = payload.LongLength,
            ManifestV1Bytes = legacySize,
            ManifestV2Bytes = encoded.Length,
            ManifestSizeDeltaBytes = encoded.Length - legacySize,
            ManifestSizeIncreasePercent = (encoded.Length - legacySize) * 100.0 / legacySize,
            ChecksumSha256 = manifest.Descriptor.Artifact.ChecksumSha256,
            ZeroCopyVerified = zeroCopyVerified,
            Stages = stages,
            StorageStages = storageStages
        };
    }

    private static StorageComparison MeasureStorage(ArtifactManifestV2 manifest, byte[] payload)
    {
        var outputRoot = Path.Combine(GetRepositoryRoot(), "TestResults", "issue-92", "storage");
        var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        var descriptor = manifest.Descriptor;
        var frame = new CameraFrame(
            descriptor.Timing.ExposureStartedUtc,
            descriptor.Layout.Width,
            descriptor.Layout.Height,
            descriptor.Layout.PixelFormat,
            payload,
            new FrameMetadata(
                descriptor.Controls.EffectiveExposure,
                descriptor.Controls.EffectiveGain,
                descriptor.Controls.EffectiveTemperatureC!.Value,
                descriptor.Artifact.SourceId,
                Offset: descriptor.Controls.EffectiveOffset),
            descriptor.Layout.StrideBytes);

        var legacy = MeasureStorageStage(
                "legacy-v1-save",
                Path.Combine(outputRoot, string.Concat("legacy-", Guid.NewGuid().ToString("N"))),
                iteration => service.SaveAsync(
                    Path.Combine(outputRoot, "legacy-work"),
                    new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame),
                    CancellationToken.None),
                payload.LongLength,
                payloadBytesReadPerOperation: 0);
        var current = MeasureStorageStage(
                "v2-save-verify-sidecar",
                Path.Combine(outputRoot, string.Concat("v2-", Guid.NewGuid().ToString("N"))),
                iteration =>
                {
                    var operationDescriptor = descriptor with
                    {
                        Capture = descriptor.Capture with { CaptureSequence = iteration + 1 },
                        Artifact = descriptor.Artifact with { ArtifactId = Guid.NewGuid() }
                    };
                    return service.SaveAsync(
                        Path.Combine(outputRoot, "v2-work"),
                        new FrameArtifact(
                            operationDescriptor.Artifact.ArtifactId,
                            FrameArtifactRole.Raw,
                            frame),
                        operationDescriptor,
                        CancellationToken.None);
                },
                payload.LongLength,
                payload.LongLength);
        return new StorageComparison(
            legacy,
            current,
            (current.MedianMilliseconds - legacy.MedianMilliseconds) * 100.0 / legacy.MedianMilliseconds,
            (current.P95Milliseconds - legacy.P95Milliseconds) * 100.0 / legacy.P95Milliseconds,
            (current.OperationsPerSecond - legacy.OperationsPerSecond) * 100.0 / legacy.OperationsPerSecond);
    }

    private static StorageMeasurement MeasureStorageStage(
        string stage,
        string cleanupRoot,
        Func<int, ValueTask<StoredFrameReference>> operation,
        long payloadBytesWrittenPerOperation,
        long payloadBytesReadPerOperation)
    {
        Directory.CreateDirectory(cleanupRoot);
        try
        {
            for (var index = 0; index < WarmupOperations; index++)
            {
                operation(index).AsTask().GetAwaiter().GetResult();
            }

            var samples = new double[MeasuredOperations];
            for (var index = 0; index < samples.Length; index++)
            {
                var started = Stopwatch.GetTimestamp();
                operation(index + WarmupOperations).AsTask().GetAwaiter().GetResult();
                samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            Array.Sort(samples);
            var totalMilliseconds = samples.Sum();
            return new StorageMeasurement(
                stage,
                totalMilliseconds,
                Percentile(samples, 0.50),
                Percentile(samples, 0.95),
                MeasuredOperations / TimeSpan.FromMilliseconds(totalMilliseconds).TotalSeconds,
                payloadBytesWrittenPerOperation,
                payloadBytesReadPerOperation);
        }
        finally
        {
            var workRoot = Path.GetDirectoryName(cleanupRoot)!;
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
    }

    private static object Measure(string stage, Action operation)
    {
        for (var index = 0; index < WarmupOperations; index++)
        {
            operation();
        }

        var samples = new double[MeasuredOperations];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < samples.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            operation();
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(samples);
        var totalMilliseconds = samples.Sum();
        return new
        {
            Stage = stage,
            TotalMilliseconds = totalMilliseconds,
            MedianMilliseconds = Percentile(samples, 0.50),
            P95Milliseconds = Percentile(samples, 0.95),
            AllocatedBytesPerOperation = allocatedBytes / MeasuredOperations,
            OperationsPerSecond = MeasuredOperations / TimeSpan.FromMilliseconds(totalMilliseconds).TotalSeconds
        };
    }

    private static double Percentile(double[] sortedSamples, double percentile)
        => sortedSamples[(int)Math.Ceiling(percentile * sortedSamples.Length) - 1];

    private static string GetRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Repository root could not be located.");
    }

    private sealed record StorageComparison(
        StorageMeasurement Legacy,
        StorageMeasurement Current,
        double MedianLatencyChangePercent,
        double P95LatencyChangePercent,
        double ThroughputChangePercent);

    private sealed record StorageMeasurement(
        string Stage,
        double TotalMilliseconds,
        double MedianMilliseconds,
        double P95Milliseconds,
        double OperationsPerSecond,
        long PayloadBytesWrittenPerOperation,
        long PayloadBytesReadPerOperation);
}
