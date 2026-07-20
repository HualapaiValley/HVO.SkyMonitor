using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientContractAndInputPerformanceTests
{
    private const int WarmupCount = 5;
    private const int MeasurementCount = 30;
    private const int FixtureSeed = 2025;
    private const string W1SourceChecksum = "8121BDC72C027790E27B08C94F969CE62F8FD961B7E0D34F6B5DFFF271C11329";
    private const string W2SourceChecksum = "10696437FD9D3A865E6FD694FAFADF2B1961162367A7A5CDCBA114DB4FA58ACB";
    private static readonly DateTimeOffset FixtureTime = DateTimeOffset.Parse(
        "2025-01-15T08:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [TestMethod]
    [TestCategory("Manual")]
    public async Task W1W2Evidence()
    {
        var revision = Git("rev-parse", "HEAD");
        var dirtyState = string.IsNullOrWhiteSpace(Git("status", "--porcelain")) ? "clean" : "dirty";
        var results = new List<PerformanceMeasurement>();
        foreach (var createWorkload in new Func<Workload>[] { CreateW1, CreateW2 })
        {
            results.AddRange(MeasureWorkload(createWorkload));
        }

        var memory = GC.GetGCMemoryInfo();
        var evidence = new
        {
            schema = "hvo-transient-contract-input-performance-v1",
            revision,
            requestedRevision = Environment.GetEnvironmentVariable("HVO_PERF_REVISION"),
            dirtyState,
            baselineRevision = "main@dc75e0d22eb3261a896d23bec63bbb85527e695f",
            baselineDisposition = "absolute-baseline",
            baselineReason = "Transient event serialization and RGGB detector conversion are net-new; shared validation is only the nearest non-equivalent path.",
            method = new
            {
                harness = "TransientContractAndInputPerformanceTests.W1W2Evidence",
                fixtureConstruction = "Synthetic deterministic arithmetic samples at W1/W2 dimensions; profile paths identify dimensional/configuration references only and are not loaded or rendered VirtualSky fixtures",
                fixtureClass = "synthetic-size-proxy",
                warmups = WarmupCount,
                measurements = MeasurementCount,
                concurrency = 1,
                outputHashingInsideTimedInterval = false,
                memorySampling = "per-stage RSS and GC heap/LOH samples with forced collections before and after each stage; process lifetime peak is not used"
            },
            environment = new
            {
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                cpu = GetCpuDescription(),
                logicalProcessorCount = Environment.ProcessorCount,
                totalAvailableMemoryBytes = memory.TotalAvailableMemoryBytes,
                sdk = "10.0.100 (global.json)",
                runtime = RuntimeInformation.FrameworkDescription,
                executionMode = "native-process",
                configuration = "Release",
                serverGarbageCollection = GCSettings.IsServerGC,
                externalIo = false
            },
            runtimeSignals = new
            {
                disposition = "N/A",
                reason = "Contracts and pure pixel conversion add no host, worker, queue, health, log, metric, or trace boundary."
            },
            results
        };
        var output = ResolveOutputPath(Environment.GetEnvironmentVariable("HVO_PERF_OUTPUT") ??
            Path.Combine("TestResults", "issue-113", "local"));
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(
            Path.Combine(output, "transient-contract-input-performance.json"),
            JsonSerializer.Serialize(evidence, EvidenceJsonOptions)).ConfigureAwait(false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<PerformanceMeasurement> MeasureWorkload(Func<Workload> createWorkload)
    {
        var workload = createWorkload();
        Assert.AreEqual(workload.ExpectedSourceChecksum, Sha256(workload.Artifact.Payload.Span), workload.Id);
        var results = new List<PerformanceMeasurement>
        {
            Measure(
                workload,
                "validation-conversion",
                "source payload bytes",
                () =>
                {
                    var result = TransientDetectorInputFactory.Create(workload.Artifact, workload.Source, workload.Levels);
                    if (!result.Validation.IsValid || result.Input is null)
                    {
                        throw new InvalidOperationException(result.Validation.ReasonCode);
                    }
                    var ownsOutput = result.Input.Ownership == TransientDetectorInputOwnership.Owned;
                    return new StageOutput(
                        result.Input.Pixels,
                        result.Input.Descriptor.InputIdentitySha256,
                        result.BytesScanned,
                        result.BytesCopied,
                        ownsOutput ? 1 : 0,
                        ownsOutput ? 2 : 1,
                        workload.Artifact.Payload.Length);
                },
                workload.Format == CameraPixelFormat.Mono16
                    ? workload.Artifact.Payload.Length
                    : workload.Artifact.Payload.Length * 2L,
                (workload.Format == CameraPixelFormat.Mono16 ? 0 : workload.Artifact.Payload.Length / 4) +
                    Linear16MaskOperations.RequiredByteLength(
                        workload.Format == CameraPixelFormat.Mono16 ? workload.Width : workload.Width / 2,
                        workload.Format == CameraPixelFormat.Mono16 ? workload.Height : workload.Height / 2),
                workload.Format == CameraPixelFormat.Mono16 ? 0 : 1,
                workload.Format == CameraPixelFormat.Mono16 ? 1 : 2,
                workload.ExpectedDetectorChecksum)
        };
        var descriptor = CreateDescriptor(workload);
        results.Add(Measure(
            workload,
            "descriptor-serialization",
            "serialized descriptor bytes",
            () =>
            {
                var bytes = TransientContractJson.Serialize(descriptor);
                return new StageOutput(bytes, descriptor.InputIdentitySha256, 0, bytes.Length, 0, 0, bytes.Length);
            },
            0,
            expectedBytesCopied: null,
            expectedFullFrameCopies: 0,
            expectedMaximumLiveBuffers: 0));
        var transientEvent = TransientTestData.CreateEvent();
        results.Add(Measure(
            workload,
            "event-serialization",
            "serialized event bytes",
            () =>
            {
                var bytes = TransientContractJson.Serialize(transientEvent);
                return new StageOutput(bytes, transientEvent.EventId.ToString("D"), 0, bytes.Length, 0, 0, bytes.Length);
            },
            0,
            expectedBytesCopied: null,
            expectedFullFrameCopies: 0,
            expectedMaximumLiveBuffers: 0));
        return results;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TransientDetectorInputDescriptorV1 CreateDescriptor(Workload workload)
    {
        var created = TransientDetectorInputFactory.Create(workload.Artifact, workload.Source, workload.Levels);
        Assert.IsTrue(created.Validation.IsValid, created.Validation.ReasonCode);
        return created.Input!.Descriptor;
    }

    private static PerformanceMeasurement Measure(
        Workload workload,
        string stage,
        string throughputBasis,
        Func<StageOutput> execute,
        long expectedBytesScanned,
        long? expectedBytesCopied,
        int expectedFullFrameCopies,
        int expectedMaximumLiveBuffers,
        string? expectedOutputChecksum = null)
    {
        for (var index = 0; index < WarmupCount; index++)
        {
            _ = execute();
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetStart = process.WorkingSet64;
        var workingSetMaximum = workingSetStart;
        var memoryStart = GC.GetGCMemoryInfo();
        var managedHeapStart = GC.GetTotalMemory(forceFullCollection: false);
        var heapMaximum = managedHeapStart;
        var lohMaximum = memoryStart.GenerationInfo[3].SizeAfterBytes;
        var fragmentationMaximum = memoryStart.FragmentedBytes;
        var elapsed = new double[MeasurementCount];
        long allocatedBytes = 0;
        double cpuMilliseconds = 0;
        string? outputChecksum = null;
        long? outputLength = null;
        long? bytesScanned = null;
        long? bytesCopied = null;
        long? throughputBytes = null;
        int? fullFrameCopies = null;
        int? maximumLiveBuffers = null;
        string? outputIdentity = null;
        for (var index = 0; index < MeasurementCount; index++)
        {
            var current = ExecuteIteration(process, execute);
            elapsed[index] = current.ElapsedMilliseconds;
            outputChecksum ??= current.OutputChecksum;
            Assert.AreEqual(outputChecksum, current.OutputChecksum, stage);
            if (expectedOutputChecksum is not null)
            {
                Assert.AreEqual(expectedOutputChecksum, current.OutputChecksum, stage);
            }
            outputIdentity ??= current.Identity;
            outputLength ??= current.OutputLength;
            bytesScanned ??= current.BytesScanned;
            bytesCopied ??= current.BytesCopied;
            throughputBytes ??= current.ThroughputBytes;
            fullFrameCopies ??= current.FullFrameCopies;
            maximumLiveBuffers ??= current.MaximumLiveBuffers;
            Assert.AreEqual(outputIdentity, current.Identity, stage);
            Assert.AreEqual(outputLength, current.OutputLength, stage);
            Assert.AreEqual(bytesScanned, current.BytesScanned, stage);
            Assert.AreEqual(bytesCopied, current.BytesCopied, stage);
            Assert.AreEqual(throughputBytes, current.ThroughputBytes, stage);
            Assert.AreEqual(fullFrameCopies, current.FullFrameCopies, stage);
            Assert.AreEqual(maximumLiveBuffers, current.MaximumLiveBuffers, stage);
            Assert.AreEqual(expectedBytesScanned, current.BytesScanned, stage);
            if (expectedBytesCopied.HasValue)
            {
                Assert.AreEqual(expectedBytesCopied.Value, current.BytesCopied, stage);
            }
            Assert.AreEqual(expectedFullFrameCopies, current.FullFrameCopies, stage);
            Assert.AreEqual(expectedMaximumLiveBuffers, current.MaximumLiveBuffers, stage);

            cpuMilliseconds += current.CpuMilliseconds;
            allocatedBytes += current.AllocatedBytes;
            process.Refresh();
            workingSetMaximum = Math.Max(workingSetMaximum, process.WorkingSet64);
            var memory = GC.GetGCMemoryInfo();
            heapMaximum = Math.Max(heapMaximum, GC.GetTotalMemory(forceFullCollection: false));
            lohMaximum = Math.Max(lohMaximum, memory.GenerationInfo[3].SizeAfterBytes);
            fragmentationMaximum = Math.Max(fragmentationMaximum, memory.FragmentedBytes);
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Array.Sort(elapsed);
        process.Refresh();
        var memoryEnd = GC.GetGCMemoryInfo();
        var managedHeapEnd = GC.GetTotalMemory(forceFullCollection: false);
        var workingSetEnd = process.WorkingSet64;
        var lohEnd = memoryEnd.GenerationInfo[3].SizeAfterBytes;
        workingSetMaximum = Math.Max(workingSetMaximum, workingSetEnd);
        heapMaximum = Math.Max(heapMaximum, managedHeapEnd);
        lohMaximum = Math.Max(lohMaximum, lohEnd);
        fragmentationMaximum = Math.Max(fragmentationMaximum, memoryEnd.FragmentedBytes);
        var averageMilliseconds = elapsed.Average();
        var sourceChecksumAfter = Sha256(workload.Artifact.Payload.Span);
        Assert.AreEqual(workload.ExpectedSourceChecksum, sourceChecksumAfter, workload.Id);
        return new PerformanceMeasurement(
            workload.Id,
            workload.ReferenceProfile,
            workload.Width,
            workload.Height,
            workload.Format.ToString(),
            workload.Artifact.Payload.Length,
            FixtureSeed,
            FixtureTime,
            workload.MaximumMagnitude,
            workload.MaximumResults,
            workload.Exposure,
            workload.Gain,
            workload.Levels.BlackLevel,
            workload.Levels.WhiteLevel,
            workload.Levels.SaturationLevel,
            workload.Artifact.Compatibility,
            stage,
            throughputBasis,
            outputLength!.Value,
            bytesScanned!.Value,
            bytesCopied!.Value,
            elapsed[elapsed.Length / 2],
            elapsed[(int)Math.Ceiling(elapsed.Length * 0.95) - 1],
            cpuMilliseconds / MeasurementCount,
            allocatedBytes / MeasurementCount,
            workingSetStart,
            workingSetMaximum,
            workingSetEnd,
            managedHeapStart,
            heapMaximum,
            managedHeapEnd,
            memoryStart.GenerationInfo[3].SizeAfterBytes,
            lohMaximum,
            lohEnd,
            memoryStart.FragmentedBytes,
            fragmentationMaximum,
            memoryEnd.FragmentedBytes,
            averageMilliseconds == 0 ? 0 : 1000 / averageMilliseconds,
            averageMilliseconds == 0 ? 0 : throughputBytes!.Value / (averageMilliseconds / 1000d),
            outputChecksum!,
            outputIdentity!,
            fullFrameCopies!.Value,
            maximumLiveBuffers!.Value,
            workload.ExpectedSourceChecksum,
            sourceChecksumAfter,
            workload.ExpectedSourceChecksum == sourceChecksumAfter,
            "absolute-baseline",
            "No equivalent transient V1 path exists on the baseline revision.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IterationMeasurement ExecuteIteration(Process process, Func<StageOutput> execute)
    {
        var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        var cpuBefore = process.TotalProcessorTime;
        var started = Stopwatch.GetTimestamp();
        var output = execute();
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var cpuAfter = process.TotalProcessorTime;
        var allocationAfter = GC.GetAllocatedBytesForCurrentThread();
        return new IterationMeasurement(
            elapsedMilliseconds,
            (cpuAfter - cpuBefore).TotalMilliseconds,
            allocationAfter - allocationBefore,
            Sha256(output.Output.Span),
            output.Output.Length,
            output.Identity,
            output.BytesScanned,
            output.BytesCopied,
            output.FullFrameCopies,
            output.MaximumLiveBuffers,
            output.ThroughputBytes);
    }

    private static Workload CreateW1()
        => CreateWorkload(
            "W1",
            "src/HVO.SkyMonitor.CameraAgent/virtual-asi174.full.json",
            1936,
            1216,
            CameraPixelFormat.Mono16,
            new TransientLinearLevelsV1(64, 4095, 4095),
            maximumResults: 2000,
            sensorIdentity: "virtual-asi174mm-electron-domain-v2",
            calibrationIdentity: "virtual-fisheye-180-equidistant-v1",
            expectedSourceChecksum: W1SourceChecksum,
            expectedDetectorChecksum: W1SourceChecksum);

    private static Workload CreateW2()
        => CreateWorkload(
            "W2",
            "src/HVO.SkyMonitor.CameraAgent/virtual-asi178mc.full.json",
            3096,
            2080,
            CameraPixelFormat.BayerRggb16,
            new TransientLinearLevelsV1(64, ushort.MaxValue, ushort.MaxValue),
            maximumResults: 300,
            sensorIdentity: "virtual-asi178mc-rggb16-v1",
            calibrationIdentity: "asi178-fujinon-fe185c057ha1-candidate-v4",
            expectedSourceChecksum: W2SourceChecksum,
            expectedDetectorChecksum: "0F50731EAC08ADBB53DC9F41D1FBF4D3886FFC038C70853D4A1C483A2916E562");

    private static Workload CreateWorkload(
        string id,
        string referenceProfile,
        int width,
        int height,
        CameraPixelFormat format,
        TransientLinearLevelsV1 levels,
        int maximumResults,
        string sensorIdentity,
        string calibrationIdentity,
        string expectedSourceChecksum,
        string expectedDetectorChecksum)
    {
        var stride = checked(width * 2);
        var pixels = GC.AllocateUninitializedArray<byte>(checked(stride * height));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = format == CameraPixelFormat.Mono16
                    ? (ushort)((FixtureSeed + 257L * (y * (long)width + x)) & 0x0FFF)
                    : (ushort)((FixtureSeed + 31L * x + 17L * y + 997L * ((y & 1) * 2 + (x & 1))) & 0xFFFF);
                var offset = y * stride + x * 2;
                pixels[offset] = (byte)value;
                pixels[offset + 1] = (byte)(value >> 8);
            }
        }
        var artifactId = id == "W1"
            ? Guid.Parse("d0000000-0000-0000-0000-000000000001")
            : Guid.Parse("d0000000-0000-0000-0000-000000000002");
        var artifact = new ProcessingArtifact(
            artifactId,
            FrameArtifactRole.Raw,
            "synthetic-linear-detector-fixture-v1",
            new string('A', 64),
            "application/x-hvo-frame",
            new FrameLayoutDescriptor(
                width,
                height,
                stride,
                format,
                FrameByteOrder.LittleEndian,
                16,
                16,
                FrameSamplePacking.ByteAligned,
                format == CameraPixelFormat.BayerRggb16 ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
                levels.BlackLevel,
                levels.WhiteLevel,
                pixels.Length),
            pixels,
            FixtureTime,
            TimeSpan.FromSeconds(20),
            new ProcessingCompatibilityIdentity(
                Path.GetFileName(referenceProfile),
                "zenith-north-up-horizontal-flip-v1",
                calibrationIdentity,
                "full-frame-v1",
                sensorIdentity,
                "night-exposure-20s-gain-150",
                "transient-input-v1"),
            ObservationStartedUtc: FixtureTime,
            ObservationEndedUtc: FixtureTime.AddSeconds(20));
        var source = new TransientSourceEvidenceReferenceV1(
            TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
            id == "W1"
                ? Guid.Parse("e0000000-0000-0000-0000-000000000001")
                : Guid.Parse("e0000000-0000-0000-0000-000000000002"),
            new TransientWholeArtifactLocatorV1(
                TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                TransientSourceLocatorKind.WholeArtifact,
                new TransientArtifactReferenceV1(
                    artifactId,
                    artifact.Role,
                    artifact.Variant,
                    artifact.RecipeIdentitySha256,
                    Sha256(pixels))),
            FixtureTime,
            FixtureTime.AddSeconds(20),
            TransientTimingQuality.Reported,
            new TransientTimingProvenanceV1("deterministic-linear-fixture", "synthetic-size-v1"));
        return new Workload(
            id,
            referenceProfile,
            width,
            height,
            format,
            artifact,
            source,
            levels,
            expectedSourceChecksum,
            expectedDetectorChecksum,
            6.5,
            maximumResults,
            TimeSpan.FromSeconds(20),
            150);
    }

    private static string Git(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to run git.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : "unavailable";
    }

    private static string GetCpuDescription()
    {
        const string prefix = "model name";
        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            var model = File.ReadLines("/proc/cpuinfo").FirstOrDefault(line =>
                line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (model is not null)
            {
                return model[(model.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
            }
        }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString();
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

    private static string Sha256(ReadOnlySpan<byte> value)
        => Convert.ToHexString(SHA256.HashData(value));

    private sealed record Workload(
        string Id,
        string ReferenceProfile,
        int Width,
        int Height,
        CameraPixelFormat Format,
        ProcessingArtifact Artifact,
        TransientSourceEvidenceReferenceV1 Source,
        TransientLinearLevelsV1 Levels,
        string ExpectedSourceChecksum,
        string ExpectedDetectorChecksum,
        double MaximumMagnitude,
        int MaximumResults,
        TimeSpan Exposure,
        double Gain);

    private sealed record StageOutput(
        ReadOnlyMemory<byte> Output,
        string Identity,
        long BytesScanned,
        long BytesCopied,
        int FullFrameCopies,
        int MaximumLiveBuffers,
        long ThroughputBytes);

    private sealed record IterationMeasurement(
        double ElapsedMilliseconds,
        double CpuMilliseconds,
        long AllocatedBytes,
        string OutputChecksum,
        int OutputLength,
        string Identity,
        long BytesScanned,
        long BytesCopied,
        int FullFrameCopies,
        int MaximumLiveBuffers,
        long ThroughputBytes);

    private sealed record PerformanceMeasurement(
        string Workload,
        string ReferenceProfile,
        int Width,
        int Height,
        string PixelFormat,
        long InputBytes,
        int FixtureSeed,
        DateTimeOffset FixtureTimeUtc,
        double MaximumMagnitude,
        int MaximumResults,
        TimeSpan Exposure,
        double Gain,
        ushort BlackLevel,
        ushort WhiteLevel,
        ushort SaturationLevel,
        ProcessingCompatibilityIdentity Compatibility,
        string Stage,
        string ThroughputBasis,
        long OutputBytes,
        long BytesScanned,
        long BytesCopied,
        double MedianWallMilliseconds,
        double P95WallMilliseconds,
        double CpuMillisecondsPerOperation,
        long AllocatedBytesPerOperation,
        long WorkingSetStartBytes,
        long WorkingSetMaximumBytes,
        long WorkingSetEndBytes,
        long ManagedHeapStartBytes,
        long ManagedHeapMaximumBytes,
        long ManagedHeapEndBytes,
        long LargeObjectHeapStartBytes,
        long LargeObjectHeapMaximumBytes,
        long LargeObjectHeapEndBytes,
        long FragmentedHeapStartBytes,
        long FragmentedHeapMaximumBytes,
        long FragmentedHeapEndBytes,
        double OperationsPerSecond,
        double ThroughputBytesPerSecond,
        string OutputChecksumSha256,
        string OutputIdentity,
        int FullFrameCopies,
        int MaximumLiveBuffers,
        string SourceChecksumBefore,
        string SourceChecksumAfter,
        bool SourceImmutable,
        string BaselineDisposition,
        string BaselineReason);
}
