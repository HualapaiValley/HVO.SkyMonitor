using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Tests.Contracts;

[TestClass]
[TestCategory("Manual")]
[TestProperty("Area", "Capture")]
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
            Result = "Per-workload descriptor size, validation, hashing and reconstruction measurements.",
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

        var reconstruction = FrameReconstructor.TryReconstruct(manifest.Descriptor, payload, out var reconstructed);
        Assert.IsTrue(reconstruction.IsValid);
        Assert.IsTrue(MemoryMarshal.TryGetArray(reconstructed!.PixelData, out var reconstructedSegment));
        var zeroCopyVerified = ReferenceEquals(payload, reconstructedSegment.Array);
        Assert.IsTrue(zeroCopyVerified);

        var stages = new[]
        {
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
        return new
        {
            Workload = workload,
            ReferenceProfile = workload == "W1" ? "virtual-asi174.full.json" : "virtual-asi178mc.full.json",
            PayloadPattern = "deterministic zero-filled bytes; descriptor-only workload has no scene seed",
            Width = width,
            Height = height,
            Format = format.ToString(),
            PayloadBytes = payload.LongLength,
            CanonicalManifestBytes = encoded.Length,
            ChecksumSha256 = manifest.Descriptor.Artifact.ChecksumSha256,
            ZeroCopyVerified = zeroCopyVerified,
            Stages = stages,
        };
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
}
