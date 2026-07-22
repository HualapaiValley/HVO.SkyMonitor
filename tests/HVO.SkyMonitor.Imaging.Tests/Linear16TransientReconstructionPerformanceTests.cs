using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Linear16TransientReconstructionPerformanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Manual")]
    public async Task Issue118W1W2_RecordsReconstructionAndProductEvidence()
    {
        Assert.AreEqual(Architecture.X64, RuntimeInformation.ProcessArchitecture);
        Assert.AreEqual("Release", typeof(Linear16TransientReconstructionPerformanceTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration);
        var results = new[]
        {
            Measure("W1", 1936, 1216, CameraPixelFormat.Mono16, observationCount: 1),
            Measure("W2", 3096, 2080, CameraPixelFormat.BayerRggb16, observationCount: 2)
        };
        var repositoryRoot = FindRepositoryRoot();
        var commit = Git(repositoryRoot, "rev-parse", "HEAD");
        var dirty = !string.IsNullOrWhiteSpace(Git(repositoryRoot, "status", "--porcelain"));
        var revision = dirty ? "local-dirty" : Environment.GetEnvironmentVariable("GITHUB_SHA") ?? commit;
        var evidence = new
        {
            Schema = "hvo-transient-reconstruction-performance-v1",
            Issue = 118,
            Revision = new
            {
                Candidate = commit,
                Base = Git(repositoryRoot, "merge-base", "HEAD", "main"),
                Branch = Git(repositoryRoot, "branch", "--show-current"),
                Dirty = dirty,
                DirtyFingerprintSha256 = DirtyFingerprint(repositoryRoot)
            },
            RecordedAtUtc = DateTimeOffset.UtcNow,
            Command = "dotnet test tests/HVO.SkyMonitor.Imaging.Tests/HVO.SkyMonitor.Imaging.Tests.csproj --configuration Release --arch x64 --filter FullyQualifiedName~Linear16TransientReconstructionPerformanceTests.Issue118W1W2_RecordsReconstructionAndProductEvidence",
            Environment = new
            {
                Framework = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
            },
            Method = "Five warmups followed by thirty measured detector-space reconstruction and five-product executions. Stage timing excludes checksum calculation; allocations cover the complete operation.",
            Results = results,
            Limitations = new[]
            {
                "Detector-space reconstruction does not infer intra-exposure timing.",
                "Saturated photometry remains a lower bound.",
                "This component harness excludes SQL and MinIO, which are measured by the issue 118 LogicHost harness."
            }
        };
        var directory = Path.Combine(repositoryRoot, "TestResults", "issue-118", revision[..Math.Min(12, revision.Length)]);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "transient-reconstruction-performance.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, JsonOptions)).ConfigureAwait(false);
        TestContext.WriteLine($"Issue #118 reconstruction evidence: {path}");

        Assert.IsTrue(results.All(result => result.Measurements == 30));
        Assert.IsTrue(results.All(result => result.OutputBytes > 0));
        Assert.IsTrue(results.All(result => result.Checksums.Count == 30));
        Assert.IsTrue(results.All(result => result.Checksums.Distinct(StringComparer.Ordinal).Count() == 1));
    }

    private static WorkloadResult Measure(
        string workload,
        int width,
        int height,
        CameraPixelFormat pixelFormat,
        int observationCount)
    {
        var detectorWidth = pixelFormat == CameraPixelFormat.BayerRggb16 ? width / 2 : width;
        var detectorHeight = pixelFormat == CameraPixelFormat.BayerRggb16 ? height / 2 : height;
        var observations = CreateObservations(detectorWidth, detectorHeight, observationCount);
        var bounds = observations.Select(item => item.Bounds).ToArray();
        var geometries = bounds.Select(item => new Linear16TransientDerivativeGeometry(
            item,
            [new PixelPoint(item.X, item.Y), new PixelPoint(item.X + item.Width - 1, item.Y + item.Height - 1)]))
            .ToArray();
        for (var index = 0; index < 5; index++)
        {
            _ = Execute(observations, geometries);
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        var elapsed = new double[30];
        var cpu = new double[30];
        var reconstructionElapsed = new double[30];
        var productElapsed = new double[30];
        var allocated = new long[30];
        var workingSet = new long[30];
        var checksums = new string[30];
        long outputBytes = 0;
        IReadOnlyList<ProductEvidence> products = [];
        var gc0Before = GC.CollectionCount(0);
        var gc1Before = GC.CollectionCount(1);
        var gc2Before = GC.CollectionCount(2);
        var lohBefore = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;
        var baselineWorkingSet = Process.GetCurrentProcess().WorkingSet64;
        for (var index = 0; index < 30; index++)
        {
            var process = Process.GetCurrentProcess();
            var cpuStarted = process.TotalProcessorTime;
            var allocatedStarted = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var execution = Execute(observations, geometries);
            elapsed[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            reconstructionElapsed[index] = execution.ReconstructionMilliseconds;
            productElapsed[index] = execution.ProductMilliseconds;
            cpu[index] = (Process.GetCurrentProcess().TotalProcessorTime - cpuStarted).TotalMilliseconds;
            allocated[index] = GC.GetTotalAllocatedBytes(precise: true) - allocatedStarted;
            workingSet[index] = Math.Max(0, Process.GetCurrentProcess().WorkingSet64 - baselineWorkingSet);
            checksums[index] = execution.Checksum;
            outputBytes = execution.OutputBytes;
            products = execution.Products;
        }
        Array.Sort(elapsed);
        Array.Sort(reconstructionElapsed);
        Array.Sort(productElapsed);
        var lohAfter = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;
        return new(
            workload,
            width,
            height,
            pixelFormat.ToString(),
            checked((long)width * height * 2),
            detectorWidth,
            detectorHeight,
            CameraPixelFormat.Mono16.ToString(),
            observationCount,
            5,
            30,
            Median(elapsed),
            Percentile(elapsed, 0.95),
            elapsed[^1],
            Median(reconstructionElapsed),
            Percentile(reconstructionElapsed, 0.95),
            Median(productElapsed),
            Percentile(productElapsed, 0.95),
            cpu.Average(),
            (long)allocated.Average(),
            workingSet.Max(),
            Process.GetCurrentProcess().PeakWorkingSet64,
            lohBefore,
            lohAfter,
            GC.CollectionCount(0) - gc0Before,
            GC.CollectionCount(1) - gc1Before,
            GC.CollectionCount(2) - gc2Before,
            outputBytes,
            1 + observationCount * 4,
            "One reconstruction frame, one packed mask, one mono preview buffer, one crop buffer, and one RGB overlay buffer; source/background frames are retained by each observation.",
            checksums,
            products);
    }

    private static ExecutionResult Execute(
        IReadOnlyList<Linear16TransientReconstructionObservation> observations,
        IReadOnlyList<Linear16TransientDerivativeGeometry> geometries)
    {
        var reconstructionStarted = Stopwatch.GetTimestamp();
        var reconstruction = Linear16TransientReconstruction.Reconstruct(observations);
        var reconstructionMilliseconds = Stopwatch.GetElapsedTime(reconstructionStarted).TotalMilliseconds;
        var productStarted = Stopwatch.GetTimestamp();
        var products = Linear16TransientDerivativeProductFactory.Create(
            reconstruction,
            geometries,
            new(CropPaddingPixels: 16, JpegQuality: 90));
        var productMilliseconds = Stopwatch.GetElapsedTime(productStarted).TotalMilliseconds;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        var productEvidence = new[]
        {
            Evidence("reconstruction", products.Reconstruction, Linear16TransientDerivativeProductFactory.Linear16MediaType),
            Evidence("mask", products.Mask, Linear16TransientDerivativeProductFactory.PackedMaskMediaType),
            Evidence("preview", products.Preview, "image/jpeg"),
            Evidence("crop", products.Crop, "image/jpeg"),
            Evidence("overlay", products.Overlay, "image/jpeg")
        };
        foreach (var payload in productEvidence)
        {
            hash.AppendData(Convert.FromHexString(payload.ChecksumSha256));
            bytes += payload.ByteLength;
        }
        return new(Convert.ToHexString(hash.GetHashAndReset()), bytes, reconstructionMilliseconds,
            productMilliseconds, productEvidence);

        static ProductEvidence Evidence(string kind, ReadOnlyMemory<byte> payload, string mediaType)
            => new(kind, payload.Length, Convert.ToHexString(SHA256.HashData(payload.Span)), mediaType);
    }

    private static Linear16TransientReconstructionObservation[] CreateObservations(
        int width,
        int height,
        int count)
    {
        return Enumerable.Range(0, count).Select(ordinal =>
        {
            var bounds = count == 2
                ? new Linear16TransientReconstructionBounds(
                    ordinal == 0 ? width / 2 - 128 : width / 2,
                    height / 2 - 64,
                    128,
                    128)
                : new Linear16TransientReconstructionBounds(
                    width / 2 - 64,
                    height / 2 - 64,
                    128,
                    128);
            var background = CreateFrame(width, height, 1000);
            var targetBytes = background.PixelData.ToArray();
            for (var y = (int)bounds.Y; y < bounds.Y + bounds.Height; y++)
            {
                for (var x = (int)bounds.X; x < bounds.X + bounds.Width; x++)
                {
                    var value = (ushort)(2000 + ordinal * 500 + (x + y) % 1000);
                    var offset = (y * width + x) * 2;
                    targetBytes[offset] = (byte)value;
                    targetBytes[offset + 1] = (byte)(value >> 8);
                }
            }
            return new Linear16TransientReconstructionObservation(
                new(width, height, width * 2, CameraPixelFormat.Mono16, targetBytes),
                background,
                new(width, height, new byte[Linear16MaskOperations.RequiredByteLength(width, height)]),
                bounds);
        }).ToArray();
    }

    private static Linear16Frame CreateFrame(
        int width,
        int height,
        ushort value)
    {
        var bytes = new byte[checked(width * height * 2)];
        for (var index = 0; index < bytes.Length; index += 2)
        {
            bytes[index] = (byte)value;
            bytes[index + 1] = (byte)(value >> 8);
        }
        return new(width, height, width * 2, CameraPixelFormat.Mono16, bytes);
    }

    private static double Median(double[] sorted) => (sorted[14] + sorted[15]) / 2;

    private static double Percentile(double[] sorted, double percentile)
        => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private static string Git(string repositoryRoot, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : "unknown";
    }

    private static string DirtyFingerprint(string repositoryRoot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n',
            Git(repositoryRoot, "status", "--porcelain"), Git(repositoryRoot, "diff", "--binary"))));
        foreach (var relativePath in Git(repositoryRoot, "ls-files", "--others", "--exclude-standard")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData(File.ReadAllBytes(Path.Combine(repositoryRoot, relativePath)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed record ExecutionResult(
        string Checksum,
        long OutputBytes,
        double ReconstructionMilliseconds,
        double ProductMilliseconds,
        IReadOnlyList<ProductEvidence> Products);

    private sealed record ProductEvidence(string Kind, int ByteLength, string ChecksumSha256, string MediaType);

    private sealed record WorkloadResult(
        string Workload,
        int Width,
        int Height,
        string SourcePixelFormat,
        long SourceBytesPerObservation,
        int DetectorWidth,
        int DetectorHeight,
        string DetectorPixelFormat,
        int ObservationCount,
        int Warmups,
        int Measurements,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds,
        double ReconstructionMedianMilliseconds,
        double ReconstructionP95Milliseconds,
        double ProductsMedianMilliseconds,
        double ProductsP95Milliseconds,
        double MeanCpuMilliseconds,
        long MeanAllocatedBytes,
        long PeakWorkingSetDeltaBytes,
        long ProcessPeakWorkingSetBytes,
        long LohBeforeBytes,
        long LohAfterBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        long OutputBytes,
        int MaximumLiveFullFrameBuffers,
        string LiveBufferAccounting,
        IReadOnlyList<string> Checksums,
        IReadOnlyList<ProductEvidence> Products);
}
