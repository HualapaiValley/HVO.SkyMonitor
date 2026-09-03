using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[DoNotParallelize]
public sealed class ProcessingHostAdapterPerformanceTests
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedPackedPreviewChecksums =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["W1"] = "42CA6AF7B5E237398972AB0DBBA89AF4A96F43A86EE60B5E7D985340E4A10980",
            ["W2"] = "A1AF7A36883C10255CD2CB519B86409E4DA5FA82AB38B640B3A8D0708D30657D"
        };

    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [TestMethod]
    [TestCategory("Manual")]
    public async Task SelectedHostAdapterEvidence()
    {
        var workloadId = GetRequiredSelection("HVO_PERF_WORKLOAD", ["W1", "W2"]);
        var host = GetRequiredSelection("HVO_PERF_HOST", ["CameraAgent", "LogicHost"]);
        var mode = GetRequiredSelection("HVO_PERF_MODE", ["warm", "cold"]);
        var warmups = mode == "cold" ? 0 : GetPositiveEnvironmentValue("HVO_PERF_WARMUPS", 5);
        var repetitions = mode == "cold" ? 1 : GetPositiveEnvironmentValue("HVO_PERF_REPETITIONS", 30);
        var fixture = workloadId switch
        {
            "W1" => CreateFixture("W1", 1936, 1216, CameraPixelFormat.Mono16),
            "W2" => CreateFixture("W2", 3096, 2080, CameraPixelFormat.BayerRggb16),
            _ => throw new InvalidOperationException($"Unsupported workload '{workloadId}'.")
        };
        var executor = new ProcessingRecipeExecutor();
        var options = JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed"));
        Func<ValueTask<ProcessingOutcome>> operation;
        if (host == "CameraAgent")
        {
            var adapter = new CameraAgentRecipeExecutionAdapter(executor);
            operation = () =>
            {
                var input = CameraAgentRecipeExecutionAdapter.CreateArtifact(
                    fixture.Config, fixture.FrameArtifact, "source");
                return adapter.ExecuteAsync(new ProcessingExecutionRequest(
                    BuiltInProcessingRecipes.EncodedPreview,
                    options,
                    ProcessingInputSelector.Raw("source"),
                    [input],
                    "adapter"), CancellationToken.None);
            };
        }
        else
        {
            var adapter = new LogicHostRecipeExecutionAdapter(executor);
            operation = () => adapter.ExecuteAsync(
                    fixture.Descriptor,
                    fixture.Payload,
                    BuiltInProcessingRecipes.EncodedPreview,
                    options,
                    ProcessingInputSelector.Raw("source"),
                    "adapter");
        }
        var measurement = await MeasureAsync(
            host, fixture, warmups, repetitions, operation).ConfigureAwait(false);

        var output = ResolveOutputPath(Environment.GetEnvironmentVariable("HVO_PERF_OUTPUT") ??
            Path.Combine("TestResults", "issue-93", "local", "host-adapters", mode, workloadId, host));
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(
            Path.Combine(output, $"host-adapter-performance-{workloadId}-{host}.json"),
            JsonSerializer.Serialize(new
            {
                schema = "hvo-processing-host-adapter-performance-v1",
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
                    serverGarbageCollection = GCSettings.IsServerGC,
                    externalIo = false
                },
                measurements = new[] { measurement }
            }, EvidenceOptions)).ConfigureAwait(false);
    }

    private static async Task<AdapterMeasurement> MeasureAsync(
        string host,
        AdapterFixture fixture,
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
        double cpuMilliseconds = 0;
        string? checksum = null;
        string? recipeIdentity = null;
        string? outputIdentity = null;
        ProcessingCompatibilityIdentity? compatibility = null;
        var process = Process.GetCurrentProcess();
        var workingSetStart = process.WorkingSet64;
        var workingSetPeak = Math.Max(workingSetStart, process.PeakWorkingSet64);
        var generation0Start = GC.CollectionCount(0);
        var generation1Start = GC.CollectionCount(1);
        var generation2Start = GC.CollectionCount(2);
        for (var iteration = 0; iteration < repetitions; iteration++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var cpuBefore = process.TotalProcessorTime;
            var started = Stopwatch.GetTimestamp();
            var outcome = await operation().ConfigureAwait(false);
            elapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            cpuMilliseconds += (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            var product = AssertProduced(outcome);
            checksum ??= product.ChecksumSha256;
            recipeIdentity ??= product.Recipe.IdentitySha256;
            outputIdentity ??= product.OutputIdentitySha256;
            compatibility ??= product.Compatibility;
            Assert.AreEqual(checksum, product.ChecksumSha256);
            Assert.AreEqual(ExpectedPackedPreviewChecksums[fixture.Id], product.ChecksumSha256);
            Assert.AreEqual(recipeIdentity, product.Recipe.IdentitySha256);
            Assert.AreEqual(outputIdentity, product.OutputIdentitySha256);
            Assert.AreEqual(compatibility, product.Compatibility);
            process.Refresh();
            workingSetPeak = Math.Max(workingSetPeak, process.PeakWorkingSet64);
        }
        process.Refresh();
        var workingSetEnd = process.WorkingSet64;
        var memory = GC.GetGCMemoryInfo();
        Array.Sort(elapsed);
        var averageMilliseconds = elapsed.Average();
        return new AdapterMeasurement(
            fixture.Id,
            host,
            fixture.Width,
            fixture.Height,
            fixture.Format.ToString(),
            fixture.Payload.Length,
            fixture.Format == CameraPixelFormat.BayerRggb16
                ? checked(fixture.Width * fixture.Height * 3L)
                : checked(fixture.Width * fixture.Height),
            elapsed[elapsed.Length / 2],
            repetitions >= 30
                ? elapsed[Math.Min(elapsed.Length - 1, (int)Math.Ceiling(elapsed.Length * 0.95) - 1)]
                : null,
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
            checksum!,
            recipeIdentity!,
            outputIdentity!,
            compatibility!);
    }

    private static ProcessingProduct AssertProduced(ProcessingOutcome outcome)
    {
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        Assert.ContainsSingle(outcome.Products);
        return outcome.Products[0];
    }

    private static AdapterFixture CreateFixture(
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
            width, height, stride, format, FrameByteOrder.LittleEndian, 16, 16,
            FrameSamplePacking.ByteAligned,
            format == CameraPixelFormat.BayerRggb16 ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            format == CameraPixelFormat.BayerRggb16 ? 64 : 0,
            format == CameraPixelFormat.BayerRggb16 ? 16_383 : ushort.MaxValue,
            payload.Length);
        var captured = DateTimeOffset.Parse(
            "2025-01-15T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var artifactId = id == "W1"
            ? Guid.Parse("93100000-0000-0000-0000-000000000001")
            : Guid.Parse("93200000-0000-0000-0000-000000000001");
        var sourceRecipe = RecipeIdentityDescriptor.Create(
            "virtual-raw", "1.0.0", "virtual-raw-v1",
            JsonSerializer.SerializeToElement(new { seed = 2025, workload = id }));
        var blackLevel = format == CameraPixelFormat.BayerRggb16 ? 64 : 0;
        var adcDepth = format == CameraPixelFormat.BayerRggb16 ? 14 : 16;
        var frame = new CameraFrame(
            captured, width, height, format, payload,
            new FrameMetadata(TimeSpan.FromSeconds(20), 150, -10, Extra: new Dictionary<string, string>
            {
                ["blackLevelAdu"] = blackLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sensorAdcBitDepth"] = adcDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }), stride);
        var frameArtifact = new FrameArtifact(
            artifactId, FrameArtifactRole.Raw, frame,
            recipeVersion: ProcessingIdentity.CreateRecipeIdentity(sourceRecipe).IdentitySha256);
        var config = CreateConfig(width, height, format);
        var processingSha = CaptureContractJson.ComputeCanonicalJsonSha256(
            JsonSerializer.SerializeToElement(config.Pipeline.Steps));
        var rigSha = RigProjectionContextFactory.CreateProfileHashSha256(config.Rig);
        var calibrationSha = HashText($"calibration:{config.Rig.Optics.CalibrationVersion}");
        var maskSha = HashText("mask:none");
        var sensorSha = CaptureContractJson.ComputeCanonicalJsonSha256(
            JsonSerializer.SerializeToElement(config.Rig.Sensor));
        var descriptor = new ReconstructionDescriptor(
            new CaptureIdentityDescriptor("agent-93", $"{id}-rig", 1,
                id == "W1" ? Guid.Parse("93100000-0000-0000-0000-000000000002") : Guid.Parse("93200000-0000-0000-0000-000000000002")),
            new CaptureTimingDescriptor(
                captured, captured, captured.AddSeconds(20), captured.AddSeconds(20.1), captured.AddSeconds(20.2)),
            new CaptureControlDescriptor(
                TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), 150, 150, null, null, null, -10),
            new CaptureProfileSet(
                new ProfileIdentityDescriptor("rig", "rig-v1", rigSha),
                new ProfileIdentityDescriptor("calibration", "none-v1", calibrationSha),
                new ProfileIdentityDescriptor("mask", "none", maskSha),
                new ProfileIdentityDescriptor("sensor", "sensor-v1", sensorSha),
                new ProfileIdentityDescriptor("processing", "pipeline-v1", processingSha)),
            layout,
            new ArtifactDescriptor(
                artifactId, FrameArtifactRole.Raw, "VirtualSky", "source", captured.AddSeconds(20.1), [],
                sourceRecipe, "application/x-hvo-frame", PayloadChecksum.ComputeSha256(payload)));
        return new AdapterFixture(id, width, height, format, payload, config, frameArtifact, descriptor);
    }

    private static CameraModuleConfig CreateConfig(int width, int height, CameraPixelFormat format) => new(
        new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix"),
        new CameraModuleDescriptor("VirtualSky"),
        new CameraRigConfig(
            new SensorProfile("fixture", width, height, 5.86,
                format == CameraPixelFormat.BayerRggb16 ? SensorColorMode.Color : SensorColorMode.Mono,
                format, SensorRecipeVersion: "sensor-v1"),
            new OpticsProfile("equidistant", 1, 180, 0, CalibrationVersion: "none-v1"),
            new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), 150, 150),
            ProfileVersion: "rig-v1"),
        CapturePipelineConfig.Empty);

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static int GetPositiveEnvironmentValue(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static int GetNonNegativeEnvironmentValue(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0 ? value : fallback;

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

    private sealed record AdapterFixture(
        string Id,
        int Width,
        int Height,
        CameraPixelFormat Format,
        ReadOnlyMemory<byte> Payload,
        CameraModuleConfig Config,
        FrameArtifact FrameArtifact,
        ReconstructionDescriptor Descriptor);

    private sealed record AdapterMeasurement(
        string Workload,
        string Host,
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
        string OutputChecksumSha256,
        string RecipeIdentitySha256,
        string OutputIdentitySha256,
        ProcessingCompatibilityIdentity Compatibility);
}
