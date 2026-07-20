using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientTemporalBackgroundPerformanceTests
{
    private const int WarmupCount = 5;
    private const int MeasurementCount = 30;
    private static readonly DateTimeOffset Epoch = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [TestMethod]
    public async Task W1W2TemporalBackgroundEvidence()
    {
        var results = new List<object>();
        foreach (var workload in new[] { Workload.W1, Workload.W2 })
        {
            results.Add(Measure(workload));
        }

        var root = GetRepositoryRoot();
        var outputDirectory = Path.Combine(root, "TestResults", "issue-115");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "temporal-background-performance.json");
        var evidence = new
        {
            SchemaVersion = "issue-115-temporal-background-performance-v1",
            RecordedUtc = DateTimeOffset.UtcNow,
            BaselineRevision = "3e475d5d20f2c8f94a3a3e5f805d8d4b3457c4c9",
            Candidate = new
            {
                HeadRevision = ReadGit(root, "rev-parse HEAD"),
                Branch = ReadGit(root, "branch --show-current"),
                DirtyState = ReadGit(root, "status --short"),
                Attribution = "Working-tree candidate over HeadRevision; production source checksums identify the measured implementation.",
                SourceChecksumsSha256 = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["src/HVO.SkyMonitor.Imaging/Linear16TemporalBackground.cs"] = FileSha256(root, "src/HVO.SkyMonitor.Imaging/Linear16TemporalBackground.cs"),
                    ["src/HVO.SkyMonitor.Imaging/Linear16DetectorInputConverter.cs"] = FileSha256(root, "src/HVO.SkyMonitor.Imaging/Linear16DetectorInputConverter.cs"),
                    ["src/HVO.SkyMonitor.Processing/TransientTemporalBackgrounds.cs"] = FileSha256(root, "src/HVO.SkyMonitor.Processing/TransientTemporalBackgrounds.cs"),
                    ["src/HVO.SkyMonitor.Processing/TransientDetectorInputs.cs"] = FileSha256(root, "src/HVO.SkyMonitor.Processing/TransientDetectorInputs.cs")
                }
            },
            Command = "dotnet test tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj --configuration Release --filter FullyQualifiedName~TransientTemporalBackgroundPerformanceTests.W1W2TemporalBackgroundEvidence",
            Environment = new
            {
                OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                Cpu = ReadCpuDescription(),
                Environment.ProcessorCount,
                TotalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                Configuration = "Release",
                ExecutionMode = "native-process",
                ExternalIo = false,
                Storage = "N/A; host artifact loading and persistence are outside this pure Processing/Imaging measurement.",
                WarmupCount,
                MeasurementCount
            },
            Method = "Five resolved whole-frame inputs; causal N-2,N-1 and centered N-2,N-1,N+1,N+2; one persistent bit mask; setup and detector conversion excluded from timed execution.",
            BufferOwnership = "Five distinct detector inputs are retained by the fixture. Each centered operation owns one background, four context-composed masks, one no-support mask, one effective mask, and bounded descriptor metadata; source frames are borrowed and not cloned.",
            BaselineComparison = new
            {
                Disposition = "Net-new temporal-background baseline",
                QualityBaseline = "The checked-in TransientStarMaskStrategyTests compares no-mask and persistent-mask residuals on normal VirtualSky W1/W2 fields.",
                ConversionBaseline = "Clean main@3e475d5 and this candidate were measured separately with TransientContractAndInputPerformanceTests; the issue evidence records deltas.",
                Interpretation = "No unexplained detector-sized clone, retained-window growth, mask-area failure, or cadence regression was observed."
            },
            RuntimeSignals = "N/A; pure host-neutral algorithms add no worker, queue, health, log, metric, trace, or external-storage boundary.",
            Results = results
        };
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(evidence, EvidenceJson)).ConfigureAwait(false);
        TestContext.WriteLine($"Issue #115 performance evidence: {outputPath}");
    }

    public TestContext TestContext { get; set; } = null!;

    private static object Measure(Workload workload)
    {
        var window = CreateWindow(workload);
        var causal = Request(
            TransientTemporalBackgroundKind.CausalProvisional,
            window,
            [TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1]);
        var centered = Request(
            TransientTemporalBackgroundKind.CenteredFinal,
            window,
            [TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1,
             TransientTemporalPosition.NPlus1, TransientTemporalPosition.NPlus2]);
        for (var index = 0; index < WarmupCount; index++)
        {
            AssertProduced(TransientTemporalBackgroundFactory.Create(causal));
            AssertProduced(TransientTemporalBackgroundFactory.Create(centered));
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetStart = process.WorkingSet64;
        var liveStart = GC.GetTotalMemory(false);
        var causalMeasurement = MeasureRequest(causal, process);
        var centeredMeasurement = MeasureRequest(centered, process);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        process.Refresh();
        var workingSetEnd = process.WorkingSet64;
        var liveEnd = GC.GetTotalMemory(false);
        var final = TransientTemporalBackgroundFactory.Create(centered);
        AssertProduced(final);
        var detectorBytes = final.Product!.Descriptor.Layout.ByteLength;
        var maskBytes = final.Product.EffectiveMask.Bits.Length;
        var maskedPixels = final.Product.EffectiveMask.Bits.Span.ToArray()
            .Sum(static value => BitOperations.PopCount(value));
        var maskedPercent = maskedPixels * 100d /
            (final.Product.Descriptor.Layout.Width * (long)final.Product.Descriptor.Layout.Height);
        var expectedOperationPayload = detectorBytes + maskBytes * 6L;
        Assert.IsLessThan(1_250, centeredMeasurement.P95Milliseconds, $"{workload.Id} centered p95 exceeded 5% of cadence.");
        Assert.IsLessThan(expectedOperationPayload + 1_000_000, centeredMeasurement.AllocatedBytesPerOperation,
            $"{workload.Id} allocations imply an unexplained detector-sized clone or accumulator.");
        Assert.IsLessThanOrEqualTo(20, maskedPercent, $"{workload.Id} persistent mask exceeded the area gate.");
        Assert.AreEqual(workload.ExpectedBackgroundChecksum, final.Product.Descriptor.BackgroundChecksumSha256);
        Assert.AreEqual(workload.ExpectedMaskChecksum, final.Product.Descriptor.EffectiveMaskChecksumSha256);
        Assert.AreEqual(
            workload.ExpectedIdentity,
            final.Product.Descriptor.BackgroundIdentitySha256,
            $"actual={final.Product.Descriptor.BackgroundIdentitySha256}");

        return new
        {
            workload.Id,
            Source = new { workload.Width, workload.Height, PixelFormat = workload.Format.ToString(), workload.SourceBytes },
            Detector = new
            {
                final.Product.Descriptor.Layout.Width,
                final.Product.Descriptor.Layout.Height,
                Bytes = detectorBytes,
                FiveInputBytes = detectorBytes * 5
            },
            ContextReadBytes = new { Causal = detectorBytes * 2, Centered = detectorBytes * 4 },
            ContextReadOperations = new { Causal = 2, Centered = 4 },
            MaskBytes = maskBytes,
            MaskedDetectorPixels = maskedPixels,
            MaskedDetectorPercent = maskedPercent,
            OutputChecksumSha256 = final.Product.Descriptor.BackgroundChecksumSha256,
            EffectiveMaskChecksumSha256 = final.Product.Descriptor.EffectiveMaskChecksumSha256,
            OutputIdentitySha256 = final.Product.Descriptor.BackgroundIdentitySha256,
            Causal = causalMeasurement,
            Centered = centeredMeasurement,
            ThroughputDetectorMiBPerSecond = detectorBytes * 4d / 1024 / 1024 /
                (centeredMeasurement.MedianMilliseconds / 1000),
            Memory = new
            {
                WorkingSetStartBytes = workingSetStart,
                WorkingSetEndBytes = workingSetEnd,
                ManagedLiveStartBytes = liveStart,
                ManagedLiveEndBytes = liveEnd,
                RetainedDeltaBytes = liveEnd - liveStart,
                ExpectedOwnedOperationPayloadBytes = expectedOperationPayload
            }
        };
    }

    private static Measurement MeasureRequest(
        TransientTemporalBackgroundRequest request,
        Process process)
    {
        var durations = new double[MeasurementCount];
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var allocationStart = GC.GetTotalAllocatedBytes(true);
        var gen0Start = GC.CollectionCount(0);
        var gen1Start = GC.CollectionCount(1);
        var gen2Start = GC.CollectionCount(2);
        var peakWorkingSet = process.WorkingSet64;
        for (var index = 0; index < durations.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var outcome = TransientTemporalBackgroundFactory.Create(request);
            durations[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            AssertProduced(outcome);
            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        }
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuStart;
        var allocated = GC.GetTotalAllocatedBytes(false) - allocationStart;
        Array.Sort(durations);
        return new Measurement(
            durations[durations.Length / 2],
            durations[(int)Math.Ceiling(durations.Length * 0.95) - 1],
            cpu.TotalMilliseconds / MeasurementCount,
            allocated / (double)MeasurementCount,
            peakWorkingSet,
            GC.CollectionCount(0) - gen0Start,
            GC.CollectionCount(1) - gen1Start,
            GC.CollectionCount(2) - gen2Start);
    }

    private static Dictionary<TransientTemporalPosition, TransientTemporalSource> CreateWindow(Workload workload)
    {
        var output = new Dictionary<TransientTemporalPosition, TransientTemporalSource>();
        foreach (var position in new[]
        {
            TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1, TransientTemporalPosition.N,
            TransientTemporalPosition.NPlus1, TransientTemporalPosition.NPlus2
        })
        {
            var sequence = 100L + (int)position;
            var sourcePixels = CreatePixels(workload, sequence);
            var checksum = Convert.ToHexString(SHA256.HashData(sourcePixels));
            var started = Epoch.AddSeconds((2 + (int)position) * 25);
            var artifactId = Guid.Parse($"b0000000-0000-0000-0000-{sequence:D12}");
            var evidenceId = Guid.Parse($"c0000000-0000-0000-0000-{sequence:D12}");
            var layout = new FrameLayoutDescriptor(
                workload.Width,
                workload.Height,
                workload.Width * 2,
                workload.Format,
                FrameByteOrder.LittleEndian,
                16,
                16,
                FrameSamplePacking.ByteAligned,
                workload.Format == CameraPixelFormat.BayerRggb16 ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
                workload.BlackLevel,
                workload.WhiteLevel,
                sourcePixels.Length);
            var compatibility = new ProcessingCompatibilityIdentity(
                $"{workload.Id}-rig", "north-up", "calibration-v1", "mask-v1", $"{workload.Id}-sensor",
                "exposure=20;gain=150", "transient-v1");
            var artifact = new ProcessingArtifact(
                artifactId,
                FrameArtifactRole.Raw,
                "linear-v1",
                new string('A', 64),
                "application/x-hvo-frame",
                layout,
                sourcePixels,
                started.AddSeconds(20),
                TimeSpan.FromSeconds(20),
                compatibility,
                sequence,
                ObservationStartedUtc: started,
                ObservationEndedUtc: started.AddSeconds(20));
            var source = new TransientSourceEvidenceReferenceV1(
                TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
                evidenceId,
                new TransientWholeArtifactLocatorV1(
                    TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                    TransientSourceLocatorKind.WholeArtifact,
                    new TransientArtifactReferenceV1(
                        artifactId,
                        FrameArtifactRole.Raw,
                        artifact.Variant,
                        artifact.RecipeIdentitySha256,
                        checksum)),
                started,
                started.AddSeconds(20),
                TransientTimingQuality.Reported,
                new TransientTimingProvenanceV1("virtual-sky", "v1"));
            var creation = TransientDetectorInputFactory.Create(
                artifact,
                source,
                new TransientLinearLevelsV1(workload.BlackLevel, workload.WhiteLevel, workload.SaturationLevel));
            Assert.IsTrue(creation.Validation.IsValid, creation.Validation.ReasonCode);
            var detectorLayout = creation.Input!.Descriptor.Layout;
            var supports = Enumerable.Range(0, workload.StarCount)
                .Select(index => new Linear16CircularMaskRegion(
                    1 + (index * 7919L % Math.Max(1, detectorLayout.Width - 2)),
                    1 + (index * 1543L % Math.Max(1, detectorLayout.Height - 2)),
                    workload.MaskRadius))
                .ToArray();
            var starMask = TransientDetectorMask.Create(
                TransientDetectorMaskKind.Star,
                new ProcessingAlgorithmIdentity("catalog-projected-star-mask", "v1"),
                Linear16MaskOperations.CreateCircularSupportMask(detectorLayout.Width, detectorLayout.Height, supports));
            var emptyMask = Linear16MaskOperations.Empty(detectorLayout.Width, detectorLayout.Height);
            var masks = new[]
            {
                TransientDetectorMaskKind.Sky,
                TransientDetectorMaskKind.ImageCircle,
                TransientDetectorMaskKind.Horizon,
                TransientDetectorMaskKind.Obstruction,
                TransientDetectorMaskKind.BadPixel
            }.Select(kind => TransientDetectorMask.Create(
                kind,
                new ProcessingAlgorithmIdentity($"{kind.ToString().ToUpperInvariant()}-mask", "v1"),
                emptyMask)).Append(starMask).ToArray();
            output[position] = new TransientTemporalSource(
                position,
                sequence,
                creation.Input,
                new TransientSensitivityV1($"{workload.Id}-response-v1", 1, 1),
                masks);
        }
        return output;
    }

    private static byte[] CreatePixels(Workload workload, long sequence)
    {
        var output = GC.AllocateUninitializedArray<byte>(workload.SourceBytes);
        for (var index = 0; index < output.Length; index += 2)
        {
            var value = (ushort)(workload.BlackLevel + ((index / 2 * 17L + sequence * 13 + 31) % 128));
            output[index] = (byte)value;
            output[index + 1] = (byte)(value >> 8);
        }
        return output;
    }

    private static TransientTemporalBackgroundRequest Request(
        TransientTemporalBackgroundKind kind,
        Dictionary<TransientTemporalPosition, TransientTemporalSource> window,
        IReadOnlyList<TransientTemporalPosition> positions)
        => new(kind, window[TransientTemporalPosition.N], positions.Select(position => window[position]).ToArray(), [], TimeSpan.FromSeconds(30));

    private static void AssertProduced(TransientTemporalBackgroundOutcome outcome)
    {
        Assert.AreEqual(TransientTemporalBackgroundStatus.Produced, outcome.Status, outcome.ReasonCode);
        Assert.IsNotNull(outcome.Product);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string ReadGit(string root, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("git could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    private static string FileSha256(string root, string relativePath)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relativePath))));

    private static string ReadCpuDescription()
    {
        if (!File.Exists("/proc/cpuinfo"))
        {
            return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        }
        var model = File.ReadLines("/proc/cpuinfo")
            .FirstOrDefault(static line => line.StartsWith("model name", StringComparison.Ordinal));
        return model?.Split(':', 2).Last().Trim() ?? "unknown";
    }

    private sealed record Measurement(
        double MedianMilliseconds,
        double P95Milliseconds,
        double CpuMillisecondsPerOperation,
        double AllocatedBytesPerOperation,
        long PeakWorkingSetBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections);

    private sealed record Workload(
        string Id,
        int Width,
        int Height,
        CameraPixelFormat Format,
        ushort BlackLevel,
        ushort WhiteLevel,
        ushort SaturationLevel,
        int StarCount,
        double MaskRadius,
        string ExpectedBackgroundChecksum,
        string ExpectedMaskChecksum,
        string ExpectedIdentity)
    {
        public static Workload W1 { get; } = new(
            "W1", 1936, 1216, CameraPixelFormat.Mono16, 64, 4095, 4000, 2000, 5,
            "22D2C07D2DEA8D4CDA8A63E19D44C7A3D83DC75FF49CBD8287FEE0ECAAB40381",
            "0D65F745BFD0A0F9330A28DCCA903DDA3F2BB066C9AB607A5EAC1B3B2A1E32F0",
            "C43846854C7AB40217F0C233BE5F6831C962E64EBFF61AFFCD6E677939E08CF5");
        public static Workload W2 { get; } = new(
            "W2", 3096, 2080, CameraPixelFormat.BayerRggb16, 64, 65535, 65000, 300, 3,
            "9A1D220DFC4C6FFDB8180DCFD4CF94D556A0528ABE2950E64F739EC9C1B73B26",
            "1E225885BE81FA8FA270DB3A605D65617DAD5604CD7C109CF1DC2818FFF07ADA",
            "A3FC8D656BD1203EDD80E236712123E95C4B1588D83A94E2A43041E07794EE6E");
        public int SourceBytes => checked(Width * Height * 2);
    }
}
