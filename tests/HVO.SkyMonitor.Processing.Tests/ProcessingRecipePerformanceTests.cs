using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ProcessingRecipePerformanceTests
{
    private static readonly string[] WorkloadIds = ["W1", "W2"];
    private static readonly string[] RecipeIds =
    [
        "direct-display-baseline",
        "linear-normalization",
        "shared-preview-packed",
        "encoded-preview-jpeg",
        "annotation-packed",
        "rolling-mean-5",
        "image-quality",
        "no-op-analyzer"
    ];
    private static readonly Dictionary<string, string> ExpectedPackedPreviewChecksums =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["W1"] = "42CA6AF7B5E237398972AB0DBBA89AF4A96F43A86EE60B5E7D985340E4A10980",
            ["W2"] = "A1AF7A36883C10255CD2CB519B86409E4DA5FA82AB38B640B3A8D0708D30657D"
        };
    private static readonly ProcessingRecipeIdentity DirectBaselineIdentity = ProcessingIdentity.CreateRecipeIdentity(
        new ProcessingRecipeDefinition("baseline", "1.0.0", "direct-v1", ProcessingOperationKind.Transform),
        JsonSerializer.SerializeToElement(new { }));

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [TestMethod]
    [TestCategory("Manual")]
    public Task WarmSelectedRecipeEvidence() => RunAsync(
        GetPositiveEnvironmentValue("HVO_PERF_WARMUPS", 5),
        GetPositiveEnvironmentValue("HVO_PERF_REPETITIONS", 30),
        "warm");

    [TestMethod]
    [TestCategory("Manual")]
    public Task ColdSelectedRecipeEvidence() => RunAsync(0, 1, "cold");

    private static async Task RunAsync(int warmups, int repetitions, string mode)
    {
        var workloadId = GetRequiredSelection("HVO_PERF_WORKLOAD", WorkloadIds);
        var recipe = GetRequiredSelection("HVO_PERF_RECIPE", RecipeIds);
        var workload = workloadId switch
        {
            "W1" => CreateWorkload("W1", 1936, 1216, CameraPixelFormat.Mono16),
            "W2" => CreateWorkload("W2", 3096, 2080, CameraPixelFormat.BayerRggb16),
            _ => throw new InvalidOperationException($"Unsupported workload '{workloadId}'.")
        };
        var operation = CreateOperation(workload, recipe);
        var result = await MeasureAsync(
            workload, recipe, warmups, repetitions, operation).ConfigureAwait(false);

        var evidence = new
        {
            schema = "hvo-processing-performance-v1",
            revision = Environment.GetEnvironmentVariable("HVO_PERF_REVISION") ?? "working-tree",
            trial = Environment.GetEnvironmentVariable("HVO_PERF_TRIAL") ?? "local",
            mode,
            warmups,
            repetitions,
            environment = new
            {
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                framework = RuntimeInformation.FrameworkDescription,
                configuration = "Release",
                concurrency = 1,
                initialBacklog = 0,
                serverGarbageCollection = GCSettings.IsServerGC,
                externalIo = false
            },
            results = new[] { result }
        };
        var output = Environment.GetEnvironmentVariable("HVO_PERF_OUTPUT") ??
            Path.Combine("TestResults", "issue-93", "local", mode, workloadId, recipe);
        output = ResolveOutputPath(output);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(
            Path.Combine(output, $"processing-performance-{workloadId}-{recipe}.json"),
            JsonSerializer.Serialize(evidence, EvidenceJsonOptions)).ConfigureAwait(false);
    }

    private static Func<ValueTask<ProcessingOutcome>> CreateOperation(Workload workload, string recipe)
    {
        if (recipe == "direct-display-baseline")
        {
            return () => ExecuteDirectPreview(workload);
        }

        var executor = new ProcessingRecipeExecutor();
        var request = recipe switch
        {
            "linear-normalization" => CreateRequest(
                BuiltInProcessingRecipes.LinearNormalization, EmptyOptions(), workload, "none"),
            "shared-preview-packed" => CreateRequest(
                BuiltInProcessingRecipes.EncodedPreview,
                JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
                workload,
                "packed"),
            "encoded-preview-jpeg" => CreateRequest(
                BuiltInProcessingRecipes.EncodedPreview, EmptyOptions(), workload, "jpeg"),
            "annotation-packed" => CreateRequest(
                BuiltInProcessingRecipes.Annotation,
                JsonSerializer.SerializeToElement(new AnnotationRecipeOptions(OutputEncoding: "Packed")),
                workload,
                "annotation",
                CreateAnnotation(workload)),
            "rolling-mean-5" => CreateRollingRequest(workload),
            "image-quality" => CreateRequest(
                BuiltInProcessingRecipes.ImageQuality, EmptyOptions(), workload, "quality"),
            "no-op-analyzer" => CreateRequest(
                BuiltInProcessingRecipes.NoOpAnalyzer, EmptyOptions(), workload, "noop"),
            _ => throw new InvalidOperationException($"Unsupported recipe '{recipe}'.")
        };
        return () => executor.ExecuteAsync(request);
    }

    private static async Task<RecipeMeasurement> MeasureAsync(
        Workload workload,
        string recipe,
        int warmups,
        int repetitions,
        Func<ValueTask<ProcessingOutcome>> operation)
    {
        for (var warmup = 0; warmup < warmups; warmup++)
        {
            AssertProduced(await operation().ConfigureAwait(false));
        }

        var elapsed = new double[repetitions];
        long allocated = 0;
        string? checksum = null;
        var process = Process.GetCurrentProcess();
        var workingSetStart = process.WorkingSet64;
        var workingSetPeak = Math.Max(workingSetStart, process.PeakWorkingSet64);
        var generation0Start = GC.CollectionCount(0);
        var generation1Start = GC.CollectionCount(1);
        var generation2Start = GC.CollectionCount(2);
        double cpuMilliseconds = 0;
        long outputBytes = 0;
        for (var iteration = 0; iteration < repetitions; iteration++)
        {
            var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            var cpuBefore = process.TotalProcessorTime;
            var started = Stopwatch.GetTimestamp();
            var outcome = await operation().ConfigureAwait(false);
            elapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            cpuMilliseconds += (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
            var product = AssertProduced(outcome);
            checksum ??= product.ChecksumSha256;
            Assert.AreEqual(checksum, product.ChecksumSha256);
            if (recipe is "direct-display-baseline" or "shared-preview-packed")
            {
                Assert.AreEqual(ExpectedPackedPreviewChecksums[workload.Id], product.ChecksumSha256);
            }
            if (recipe == "direct-display-baseline")
            {
                Assert.AreEqual(product.ChecksumSha256, ProcessingIdentity.ComputePayloadSha256(product.Payload));
            }
            outputBytes = product.Payload.Length;
            process.Refresh();
            workingSetPeak = Math.Max(workingSetPeak, process.PeakWorkingSet64);
        }
        process.Refresh();
        var workingSetEnd = process.WorkingSet64;
        var memory = GC.GetGCMemoryInfo();
        Array.Sort(elapsed);
        var median = elapsed[elapsed.Length / 2];
        double? p95 = repetitions >= 30
            ? elapsed[Math.Min(elapsed.Length - 1, (int)Math.Ceiling(elapsed.Length * 0.95) - 1)]
            : null;
        var averageMilliseconds = elapsed.Average();
        return new RecipeMeasurement(
            workload.Id,
            recipe,
            workload.Width,
            workload.Height,
            workload.Format.ToString(),
            workload.Artifact.Payload.Length,
            outputBytes,
            median,
            p95,
            cpuMilliseconds / repetitions,
            allocated / repetitions,
            workingSetStart,
            workingSetPeak,
            workingSetEnd,
            memory.HeapSizeBytes,
            memory.FragmentedBytes,
            memory.GenerationInfo[3].SizeAfterBytes,
            GC.CollectionCount(0) - generation0Start,
            GC.CollectionCount(1) - generation1Start,
            GC.CollectionCount(2) - generation2Start,
            averageMilliseconds == 0 ? 0 : 1000 / averageMilliseconds,
            averageMilliseconds == 0 ? 0 :
                workload.Artifact.Payload.Length / 1024d / 1024d / (averageMilliseconds / 1000d),
            checksum!,
            recipe switch
            {
                "direct-display-baseline" => workload.Format == CameraPixelFormat.BayerRggb16 ? 3 : 2,
                "linear-normalization" => 2,
                "shared-preview-packed" => workload.Format == CameraPixelFormat.BayerRggb16 ? 3 : 2,
                "rolling-mean-5" => 10,
                "annotation-packed" => workload.Format == CameraPixelFormat.BayerRggb16 ? 7 : 5,
                "encoded-preview-jpeg" => workload.Format == CameraPixelFormat.BayerRggb16 ? 5 : 3,
                _ => 1
            },
            recipe switch
            {
                "direct-display-baseline" => workload.Format == CameraPixelFormat.BayerRggb16 ? 2 : 1,
                "linear-normalization" => 1,
                "shared-preview-packed" => workload.Format == CameraPixelFormat.BayerRggb16 ? 2 : 1,
                "encoded-preview-jpeg" => workload.Format == CameraPixelFormat.BayerRggb16 ? 4 : 2,
                "annotation-packed" => workload.Format == CameraPixelFormat.BayerRggb16 ? 6 : 4,
                "rolling-mean-5" => 1,
                _ => 0
            },
            recipe == "rolling-mean-5" ? checked(workload.Artifact.Payload.Length * 5L) : 0,
            recipe switch
            {
                "rolling-mean-5" => "O(width*height*source-count)",
                "annotation-packed" => "O(width*height+geometry)",
                "no-op-analyzer" => "O(1)",
                _ => "O(width*height)"
            },
            0,
            0,
            0,
            0);
    }

    private static ValueTask<ProcessingOutcome> ExecuteDirectPreview(Workload workload)
    {
        var layout = workload.Artifact.Layout!;
        ReadOnlyMemory<byte> pixels = workload.Format == CameraPixelFormat.BayerRggb16
            ? BayerRggb16Demosaicer.DemosaicToRgb24(
                layout.Width, layout.Height, workload.Artifact.Payload, layout.StrideBytes)
            : Mono16DisplayStretch.Apply(
                layout.Width, layout.Height, workload.Artifact.Payload, layout.StrideBytes);
        var product = new ProcessingProduct(
            FrameArtifactRole.Preview,
            "baseline",
            new string('0', 64),
            "application/x-hvo-packed-image",
            null,
            pixels,
            ExpectedPackedPreviewChecksums[workload.Id],
            DirectBaselineIdentity,
            [],
            [workload.Artifact.ArtifactId],
            workload.Artifact.Integration,
            workload.Artifact.Compatibility);
        return ValueTask.FromResult(ProcessingOutcome.Produced(product));
    }

    private static ProcessingProduct AssertProduced(ProcessingOutcome outcome)
    {
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        Assert.ContainsSingle(outcome.Products);
        return outcome.Products[0];
    }

    private static ProcessingExecutionRequest CreateRequest(
        string recipe,
        JsonElement options,
        Workload workload,
        string variant,
        ProcessingAnnotationInput? annotation = null) =>
        new(recipe, options, ProcessingInputSelector.Raw("source"), [workload.Artifact], variant, annotation);

    private static ProcessingExecutionRequest CreateRollingRequest(Workload workload)
    {
        var sources = Enumerable.Range(0, 5).Select(index => workload.Artifact with
        {
            ArtifactId = CreateGuid(workload.Id, index),
            CreatedUtc = workload.Artifact.CreatedUtc.AddSeconds(index),
            Payload = AddSampleOffset(workload.Artifact, index * 113)
        }).ToArray();
        return new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.RollingMean,
            JsonSerializer.SerializeToElement(new RollingMeanOptions(5)),
            ProcessingInputSelector.Raw("source"),
            sources,
            "mean-5");
    }

    private static ProcessingAnnotationInput CreateAnnotation(Workload workload)
    {
        var objects = Enumerable.Range(0, workload.Id == "W1" ? 2000 : 309)
            .Select(index => new ProjectedAnnotationObject(
                $"object:{index}",
                $"OBJ{index}",
                new PixelPoint(
                    (index * 7919L + 2025) % workload.Width,
                    (index * 1543L + 2025) % workload.Height),
                DrawLabel: index < 50))
            .ToArray();
        var segmentCount = workload.Id == "W1" ? 163 : 155;
        var segments = Enumerable.Range(0, segmentCount)
            .Select(index => new ProjectedAnnotationSegment(
                $"C{index % 20}",
                objects[index].Pixel,
                objects[(index + 17) % objects.Length].Pixel))
            .ToArray();
        return new ProcessingAnnotationInput(
            objects,
            segments,
            new PreviewTransform(1, 1),
            null,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{workload.Id}-annotation-seed-2025"))));
    }

    private static Workload CreateWorkload(
        string id,
        int width,
        int height,
        CameraPixelFormat format)
    {
        var stride = checked(width * 2);
        var payload = new byte[checked(stride * height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = format == CameraPixelFormat.Mono16
                    ? (ushort)((2025L + 257L * (y * (long)width + x)) & 0xFFFF)
                    : (ushort)((64 + 2025 + 31 * x + 17 * y + 997 * ((y & 1) * 2 + (x & 1))) & 0x3FFF);
                var offset = y * stride + x * 2;
                payload[offset] = (byte)value;
                payload[offset + 1] = (byte)(value >> 8);
            }
        }

        var layout = new FrameLayoutDescriptor(
            width,
            height,
            stride,
            format,
            FrameByteOrder.LittleEndian,
            16,
            16,
            FrameSamplePacking.ByteAligned,
            format == CameraPixelFormat.BayerRggb16 ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            format == CameraPixelFormat.BayerRggb16 ? 64 : 0,
            format == CameraPixelFormat.BayerRggb16 ? 16383 : ushort.MaxValue,
            payload.Length);
        var artifact = new ProcessingArtifact(
            CreateGuid(id, 0),
            FrameArtifactRole.Raw,
            "source",
            new string('0', 64),
            "application/x-hvo-frame",
            layout,
            payload,
            DateTimeOffset.Parse("2025-01-15T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(20),
            new ProcessingCompatibilityIdentity(
                $"{id}-rig", "north-up", "none", "full", $"{id}-sensor", "night", "pipeline-v1"));
        return new Workload(id, width, height, format, artifact);
    }

    private static ReadOnlyMemory<byte> AddSampleOffset(ProcessingArtifact artifact, int offset)
    {
        var output = artifact.Payload.ToArray();
        var maximum = artifact.Layout!.PixelFormat == CameraPixelFormat.BayerRggb16 ? 0x3FFF : 0xFFFF;
        for (var index = 0; index < output.Length; index += 2)
        {
            var value = (output[index] | output[index + 1] << 8) + offset;
            value &= maximum;
            output[index] = (byte)value;
            output[index + 1] = (byte)(value >> 8);
        }
        return output;
    }

    private static Guid CreateGuid(string workload, int index)
    {
        var bytes = new byte[16];
        bytes[0] = 0x93;
        bytes[1] = workload == "W1" ? (byte)1 : (byte)2;
        BitConverter.TryWriteBytes(bytes.AsSpan(12), index + 1);
        return new Guid(bytes);
    }

    private static JsonElement EmptyOptions() => JsonSerializer.SerializeToElement(new { });

    private static int GetPositiveEnvironmentValue(string name, int fallback)
    {
        var value = GetNonNegativeEnvironmentValue(name, fallback);
        return value > 0 ? value : fallback;
    }

    private static int GetNonNegativeEnvironmentValue(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0
            ? value
            : fallback;

    private static string GetRequiredSelection(string name, IReadOnlyList<string> supported)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null || !supported.Contains(value, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"{name} must be one of: {string.Join(", ", supported)}.");
        }
        return value;
    }

    private static string ResolveOutputPath(string output)
    {
        if (Path.IsPathRooted(output))
        {
            return output;
        }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return Path.Combine(directory.FullName, output);
            }
        }
        throw new DirectoryNotFoundException("Repository root was not found for performance output.");
    }

    private sealed record Workload(
        string Id,
        int Width,
        int Height,
        CameraPixelFormat Format,
        ProcessingArtifact Artifact);

    private sealed record RecipeMeasurement(
        string Workload,
        string Recipe,
        int Width,
        int Height,
        string PixelFormat,
        long InputBytes,
        long OutputBytes,
        double MedianMilliseconds,
        double? P95Milliseconds,
        double CpuMillisecondsPerOperation,
        long AllocatedBytesPerOperation,
        long WorkingSetStartBytes,
        long WorkingSetPeakBytes,
        long WorkingSetEndBytes,
        long ManagedHeapBytes,
        long FragmentedHeapBytes,
        long LargeObjectHeapBytes,
        int Generation0Collections,
        int Generation1Collections,
        int Generation2Collections,
        double OperationsPerSecond,
        double InputMiBPerSecond,
        string OutputChecksumSha256,
        int MaximumLiveFullFrameBuffers,
        int DeclaredFullFrameCopies,
        long InputWindowBytes,
        string Complexity,
        long FilesystemOperations,
        long SqlOperations,
        long MinioOperations,
        long NetworkOperations);
}
