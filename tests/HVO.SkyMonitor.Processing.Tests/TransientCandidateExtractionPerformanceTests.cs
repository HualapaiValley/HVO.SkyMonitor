using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientCandidateExtractionPerformanceTests
{
    private const int WarmupCount = 5;
    private const int MeasurementCount = 30;
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Linear16TransientExtractionOptions Options = new(
        50,
        4,
        500,
        32,
        16,
        4096,
        100_000,
        4,
        0.9);
    private static readonly TransientCandidateExtractionOptionsV1 ProcessingOptions = new(
        50, 4, 500, 32, 16, 4096, 100_000, 4, 0.9);
    private static readonly TransientDeterministicAssessmentOptionsV1 AssessmentOptions = new(
        5, 3, 8, 3, 1.8, 0.5, 10, 100_000, 3, 3, 3, 1_000, 30, 2);

    [TestMethod]
    public async Task W1W2CandidateExtractionEvidence()
    {
        var results = new List<object>();
        foreach (var workload in new[] { Workload.W1, Workload.W2 })
        {
            results.Add(Measure(workload));
        }

        var root = GetRepositoryRoot();
        var outputDirectory = Path.Combine(root, "TestResults", "issue-121");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "candidate-extraction-performance.json");
        var evidence = new
        {
            SchemaVersion = "issue-121-candidate-extraction-performance-v1",
            RecordedUtc = DateTimeOffset.UtcNow,
            BaselineRevision = "b595e87dbad642646d6cc41bbd7880c6e393a198",
            Candidate = new
            {
                HeadRevision = ReadGit(root, "rev-parse HEAD"),
                Branch = ReadGit(root, "branch --show-current"),
                DirtyState = ReadGit(root, "status --short"),
                Attribution = "Working-tree candidate over HeadRevision; source checksums identify the measured implementation.",
                SourceChecksumsSha256 = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["src/HVO.SkyMonitor.Imaging/Linear16TransientExtraction.cs"] =
                        FileSha256(root, "src/HVO.SkyMonitor.Imaging/Linear16TransientExtraction.cs"),
                    ["src/HVO.SkyMonitor.Processing/TransientCandidateExtraction.cs"] =
                        FileSha256(root, "src/HVO.SkyMonitor.Processing/TransientCandidateExtraction.cs"),
                    ["src/HVO.SkyMonitor.Processing/TransientAssessment.cs"] =
                        FileSha256(root, "src/HVO.SkyMonitor.Processing/TransientAssessment.cs"),
                    ["src/HVO.SkyMonitor.Processing/TransientTemporalBackgrounds.cs"] =
                        FileSha256(root, "src/HVO.SkyMonitor.Processing/TransientTemporalBackgrounds.cs"),
                    ["tests/HVO.SkyMonitor.Processing.Tests/TransientCandidateExtractionPerformanceTests.cs"] =
                        FileSha256(root, "tests/HVO.SkyMonitor.Processing.Tests/TransientCandidateExtractionPerformanceTests.cs"),
                    ["tests/HVO.SkyMonitor.Processing.Tests/TransientTestData.cs"] =
                        FileSha256(root, "tests/HVO.SkyMonitor.Processing.Tests/TransientTestData.cs")
                }
            },
            Command = "dotnet test tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj --configuration Release --filter FullyQualifiedName~TransientCandidateExtractionPerformanceTests.W1W2CandidateExtractionEvidence",
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
                Storage = "N/A; this measures host-neutral in-memory Imaging and Processing stages.",
                WarmupCount,
                MeasurementCount,
                ExposureDurationSeconds = 1,
                SourceCadenceSeconds = 5,
                TemporalContextDurationSeconds = 5,
                TimingGaps = "None in the synthetic resolved-input stage; temporal resolution is measured by #115.",
                Concurrency = 1,
                InitialBacklog = 0
            },
            Method = "Measures the borrowed-buffer Imaging primitive, then the validated Processing extraction receipt and deterministic assessment over the same elongated residual. Fixture construction, temporal-background creation, and RGGB detector conversion are excluded.",
            BufferOwnership = "Target/background are borrowed. Extraction owns state/traversal arrays, foreground-support arrays, bounded component/profile metadata, candidates, and a bounded receipt; no residual frame or retained full-frame output is created.",
            BaselineComparison = new
            {
                Disposition = "Net-new extraction path",
                NearestBaseline = "#115 temporal background evidence provides the nearest full-frame Processing/Imaging scan baseline.",
                Interpretation = "Absolute candidate latency/allocation baseline; no equivalent extraction existed on b595e87. Later #119 locks the full scenario/confusion baseline."
            },
            Composition = new
            {
                DetectorStage = "This harness starts from resolved Mono16 detector buffers. W2 source dimensions and bytes describe the canonical Bayer source but are not scanned by this substage.",
                SourceConversionEvidence = "TestResults/issue-113/transient-contract-input-performance.json",
                TemporalWindowEvidence = "TestResults/issue-115/temporal-background-performance.json",
                Interpretation = "Review source conversion/window evidence together with these detector extraction and assessment measurements; costs are not represented as an end-to-end sum because retained windows overlap."
            },
            RuntimeSignals = "N/A; host-neutral extraction adds no queue, worker, health, logging, metric, trace, persistence, or external I/O boundary.",
            Results = results
        };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(evidence, EvidenceJson)).ConfigureAwait(false);
        TestContext.WriteLine($"Issue #121 performance evidence: {outputPath}");
    }

    public TestContext TestContext { get; set; } = null!;

    private static object Measure(Workload workload)
    {
        var fixture = CreateFixture(workload);
        for (var index = 0; index < WarmupCount; index++)
        {
            AssertResult(Linear16TransientExtraction.Extract(
                fixture.Target, fixture.Background, fixture.HardMask, fixture.SaturationMask, Options));
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetStart = process.WorkingSet64;
        var liveStart = GC.GetTotalMemory(false);
        var durations = new double[MeasurementCount];
        var allocationStart = GC.GetTotalAllocatedBytes(true);
        var cpuStart = process.TotalProcessorTime;
        var peakWorkingSet = workingSetStart;
        var gen0Start = GC.CollectionCount(0);
        var gen1Start = GC.CollectionCount(1);
        var gen2Start = GC.CollectionCount(2);
        for (var index = 0; index < durations.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var result = Linear16TransientExtraction.Extract(
                fixture.Target, fixture.Background, fixture.HardMask, fixture.SaturationMask, Options);
            durations[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            AssertResult(result);
            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        }
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuStart;
        var allocated = GC.GetTotalAllocatedBytes(false) - allocationStart;
        Array.Sort(durations);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        process.Refresh();
        var workingSetEnd = process.WorkingSet64;
        var liveEnd = GC.GetTotalMemory(false);
        var final = Linear16TransientExtraction.Extract(
            fixture.Target, fixture.Background, fixture.HardMask, fixture.SaturationMask, Options);
        AssertResult(final);
        var median = durations[durations.Length / 2];
        var p95 = durations[(int)Math.Ceiling(durations.Length * 0.95) - 1];
        var allocatedPerOperation = allocated / (double)MeasurementCount;
        var detectorPixels = (long)workload.DetectorWidth * workload.DetectorHeight;
        Assert.IsLessThan(1_250, p95, $"{workload.Id} extraction p95 exceeded 25% of five-second cadence.");
        Assert.IsLessThan(detectorPixels * 6 + 2_000_000, allocatedPerOperation,
            $"{workload.Id} allocations exceeded the declared state/queue plus metadata boundary.");
        var outputBytes = JsonSerializer.SerializeToUtf8Bytes(final.Components).Length;
        var outputIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(final.Components);
        Assert.AreEqual(workload.ExpectedOutputIdentitySha256, outputIdentity, workload.Id);
        var processing = MeasureProcessing(fixture.Request, workload.Id);

        return new
        {
            workload.Id,
            Source = new { workload.SourceWidth, workload.SourceHeight, PixelFormat = workload.SourceFormat.ToString(), workload.SourceBytes },
            Detector = new
            {
                Width = workload.DetectorWidth,
                Height = workload.DetectorHeight,
                BytesPerFrame = workload.DetectorBytes,
                BorrowedFrameCount = 2,
                BorrowedBytes = workload.DetectorBytes * 2L
            },
            Scan = new
            {
                final.BytesScanned,
                PixelCount = detectorPixels,
                FullFrameReadOperations = 2,
                MaskReadOperations = 2,
                FilesystemBytes = 0,
                DatabaseOperations = 0,
                NetworkBytes = 0
            },
            Output = new
            {
                CandidateCount = final.Components.Count,
                CandidateRatePerFrame = final.Components.Count,
                GeometryBytes = outputBytes,
                OverlayBytes = "N/A; V1 emits authoritative geometry and overlay derivatives are outside issue #121.",
                IdentitySha256 = outputIdentity,
                final.ForegroundPixelCount,
                final.HardMaskedPixelCount,
                final.SaturatedPixelCount
            },
            Latency = new { MedianMilliseconds = median, P95Milliseconds = p95 },
            CpuMillisecondsPerFrame = cpu.TotalMilliseconds / MeasurementCount,
            AllocatedBytesPerOperation = allocatedPerOperation,
            AllocationRateBytesPerSecond = allocatedPerOperation / (median / 1000),
            ThroughputFramesPerSecond = 1000 / median,
            ThroughputDetectorMiBPerSecond = final.BytesScanned / 1024d / 1024d / (median / 1000),
            CostMillisecondsPerElapsedInputSecond = median / 5,
            Memory = new
            {
                WorkingSetStartBytes = workingSetStart,
                WorkingSetEndBytes = workingSetEnd,
                PeakWorkingSetBytes = peakWorkingSet,
                ManagedLiveStartBytes = liveStart,
                ManagedLiveEndBytes = liveEnd,
                RetainedDeltaBytes = liveEnd - liveStart,
                TemporaryStateBytes = detectorPixels,
                TemporaryResidualBytes = 0,
                TemporaryTraversalBytes = detectorPixels * sizeof(int),
                LohArraysPerOperation = 2,
                RetainedWindowBytes = 0
            },
            Collections = new
            {
                Gen0 = GC.CollectionCount(0) - gen0Start,
                Gen1 = GC.CollectionCount(1) - gen1Start,
                Gen2 = GC.CollectionCount(2) - gen2Start
            },
            Processing = processing,
            Backlog = new { Count = 0, Bytes = 0, OldestAgeSeconds = 0, DrainRate = "N/A; synchronous pure algorithm" }
        };
    }

    private static object MeasureProcessing(TransientCandidateExtractionRequest request, string workloadId)
    {
        for (var index = 0; index < WarmupCount; index++)
        {
            AssertExtraction(TransientCandidateExtractionFactory.Create(request));
        }
        var extractionMeasurement = MeasureStage(() =>
            AssertExtraction(TransientCandidateExtractionFactory.Create(request)));
        Assert.IsLessThan(1_250, extractionMeasurement.P95Milliseconds,
            $"{workloadId} Processing extraction p95 exceeded 25% of five-second cadence.");

        var extraction = TransientCandidateExtractionFactory.Create(request);
        AssertExtraction(extraction);
        var extractionReceipt = TransientCandidateExtractionJson.Serialize(extraction.Descriptor!);
        var observation = TransientObservationFactory.CreateAssessmentObservation(new TransientObservationPromotionRequest(
            extraction.Candidates[0].CandidateId,
            Guid.Parse("f1000000-0000-0000-0000-000000000001"),
            0,
            extraction.Descriptor!));
        var assessmentRequest = new TransientAssessmentExecutionRequest(
            extraction.Candidates[0].EventId,
            Guid.Parse("f2000000-0000-0000-0000-000000000001"),
            request.CreatedUtc.AddSeconds(1),
            TransientAssessmentAuthority.Provisional,
            [observation],
            AssessmentOptions,
            []);
        for (var index = 0; index < WarmupCount; index++)
        {
            AssertAssessment(TransientAssessmentFactory.Create(assessmentRequest));
        }
        var assessmentMeasurement = MeasureStage(() =>
            AssertAssessment(TransientAssessmentFactory.Create(assessmentRequest)));
        Assert.IsLessThan(50, assessmentMeasurement.P95Milliseconds,
            $"{workloadId} assessment p95 exceeded its evidence threshold.");
        var assessment = TransientAssessmentFactory.Create(assessmentRequest);
        AssertAssessment(assessment);
        var assessmentReceipt = TransientAssessmentJson.Serialize(assessment.Descriptor!);
        return new
        {
            CandidateExtraction = extractionMeasurement,
            ExtractionReceiptBytes = extractionReceipt.Length,
            ExtractionReceiptIdentitySha256 = Convert.ToHexString(SHA256.HashData(extractionReceipt)),
            Assessment = assessmentMeasurement,
            AssessmentReceiptBytes = assessmentReceipt.Length,
            AssessmentReceiptIdentitySha256 = Convert.ToHexString(SHA256.HashData(assessmentReceipt))
        };
    }

    private static StageMeasurement MeasureStage(Action action)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        var durations = new double[MeasurementCount];
        var allocationStart = GC.GetTotalAllocatedBytes(true);
        var cpuStart = process.TotalProcessorTime;
        for (var index = 0; index < durations.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            action();
            durations[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuStart;
        var allocated = GC.GetTotalAllocatedBytes(false) - allocationStart;
        Array.Sort(durations);
        return new StageMeasurement(
            durations[durations.Length / 2],
            durations[(int)Math.Ceiling(durations.Length * 0.95) - 1],
            cpu.TotalMilliseconds / MeasurementCount,
            allocated / (double)MeasurementCount);
    }

    private static Fixture CreateFixture(Workload workload)
    {
        var samples = checked(workload.DetectorWidth * workload.DetectorHeight);
        var backgroundBytes = new byte[samples * 2];
        var targetBytes = new byte[samples * 2];
        for (var index = 0; index < samples; index++)
        {
            var value = (ushort)(64 + index % 17);
            targetBytes[index * 2] = backgroundBytes[index * 2] = (byte)value;
            targetBytes[index * 2 + 1] = backgroundBytes[index * 2 + 1] = (byte)(value >> 8);
        }
        var startX = workload.DetectorWidth / 4;
        var endX = workload.DetectorWidth * 3 / 4;
        for (var x = startX; x < endX; x++)
        {
            var y = workload.DetectorHeight / 3 + (x - startX) / 8;
            for (var offsetY = -1; offsetY <= 1; offsetY++)
            {
                var index = (y + offsetY) * workload.DetectorWidth + x;
                var value = (ushort)(400 + (x - startX) % 500);
                targetBytes[index * 2] = (byte)value;
                targetBytes[index * 2 + 1] = (byte)(value >> 8);
            }
        }
        var stride = workload.DetectorWidth * 2;
        var request = CreateProcessingRequest(workload, targetBytes, backgroundBytes);
        return new Fixture(
            new Linear16Frame(workload.DetectorWidth, workload.DetectorHeight, stride, CameraPixelFormat.Mono16, targetBytes),
            new Linear16Frame(workload.DetectorWidth, workload.DetectorHeight, stride, CameraPixelFormat.Mono16, backgroundBytes),
            Linear16MaskOperations.Empty(workload.DetectorWidth, workload.DetectorHeight),
            Linear16MaskOperations.Empty(workload.DetectorWidth, workload.DetectorHeight),
            request);
    }

    private static TransientCandidateExtractionRequest CreateProcessingRequest(
        Workload workload,
        byte[] targetBytes,
        byte[] backgroundBytes)
    {
        var positions = new[]
        {
            TransientTemporalPosition.NMinus2,
            TransientTemporalPosition.NMinus1,
            TransientTemporalPosition.N,
            TransientTemporalPosition.NPlus1,
            TransientTemporalPosition.NPlus2
        };
        var window = positions.ToDictionary(
            static position => position,
            position => CreateSource(
                workload,
                position,
                position == TransientTemporalPosition.N ? targetBytes : backgroundBytes));
        var background = TransientTemporalBackgroundFactory.Create(new TransientTemporalBackgroundRequest(
            TransientTemporalBackgroundKind.CenteredFinal,
            window[TransientTemporalPosition.N],
            positions.Where(static position => position != TransientTemporalPosition.N)
                .Select(position => window[position]).ToArray(),
            [],
            TimeSpan.FromSeconds(30)));
        Assert.AreEqual(TransientTemporalBackgroundStatus.Produced, background.Status, background.ReasonCode);
        var byEvidence = window.Values.ToDictionary(static source => source.Input.Descriptor.Source.EvidenceId);
        var slots = Enumerable.Range(1, ProcessingOptions.MaximumCandidates).Select(index =>
            new TransientCandidateIdentitySlot(
                Guid.Parse($"f3000000-0000-0000-0000-{index:D12}"),
                Guid.Parse($"f4000000-0000-0000-0000-{index:D12}"))).ToArray();
        return new TransientCandidateExtractionRequest(
            $"performance-{workload.Id}",
            TransientTestData.Epoch.AddMinutes(1),
            window[TransientTemporalPosition.N],
            background.Product!,
            background.Product!.Descriptor.Sources.Select(source => byEvidence[source.EvidenceId]).ToArray(),
            slots,
            ProcessingOptions,
            CenteredContextConverged: true);
    }

    private static TransientTemporalSource CreateSource(
        Workload workload,
        TransientTemporalPosition position,
        byte[] payload)
    {
        var sequence = 100 + (int)position;
        var started = TransientTestData.Epoch.AddSeconds((int)position * 5 + 20);
        var ended = started.AddSeconds(1);
        var artifactId = Guid.Parse($"f5000000-0000-0000-0000-{sequence:D12}");
        var evidenceId = Guid.Parse($"f6000000-0000-0000-0000-{sequence:D12}");
        var template = TransientTestData.CreateDetectorSource(CameraPixelFormat.Mono16);
        var artifact = template.Artifact with
        {
            ArtifactId = artifactId,
            Payload = payload,
            CaptureSequence = sequence,
            CreatedUtc = ended,
            ObservationStartedUtc = started,
            ObservationEndedUtc = ended,
            Layout = template.Artifact.Layout! with
            {
                Width = workload.DetectorWidth,
                Height = workload.DetectorHeight,
                StrideBytes = workload.DetectorWidth * 2,
                ByteLength = payload.Length
            }
        };
        var source = template.Source with
        {
            EvidenceId = evidenceId,
            Locator = template.Source.Locator with
            {
                Artifact = template.Source.Locator.Artifact with
                {
                    ArtifactId = artifactId,
                    ChecksumSha256 = Convert.ToHexString(SHA256.HashData(payload))
                }
            },
            ObservationStartedUtc = started,
            ObservationEndedUtc = ended
        };
        var input = TransientDetectorInputFactory.Create(artifact, source, template.Levels);
        Assert.IsTrue(input.Validation.IsValid, input.Validation.ReasonCode);
        var masks = new[]
        {
            TransientDetectorMaskKind.Sky,
            TransientDetectorMaskKind.ImageCircle,
            TransientDetectorMaskKind.Horizon,
            TransientDetectorMaskKind.Obstruction,
            TransientDetectorMaskKind.BadPixel,
            TransientDetectorMaskKind.Star
        }.Select(kind => TransientDetectorMask.Create(
            kind,
            new ProcessingAlgorithmIdentity($"performance-{kind}-mask", "v1"),
            Linear16MaskOperations.Empty(workload.DetectorWidth, workload.DetectorHeight))).ToArray();
        return new TransientTemporalSource(
            position,
            sequence,
            input.Input!,
            new TransientSensitivityV1("performance-response-v1", 1, 1),
            masks);
    }

    private static void AssertResult(Linear16TransientExtractionResult result)
    {
        Assert.IsFalse(result.CandidateLimitExceeded);
        Assert.HasCount(1, result.Components);
        Assert.IsGreaterThan(100, result.Components[0].LengthPixels);
    }

    private static void AssertExtraction(TransientCandidateExtractionOutcome outcome)
    {
        Assert.AreEqual(TransientCandidateExtractionStatus.Produced, outcome.Status, outcome.ReasonCode);
        Assert.HasCount(1, outcome.Candidates);
        Assert.IsNotNull(outcome.Descriptor);
    }

    private static void AssertAssessment(TransientAssessmentExecutionOutcome outcome)
    {
        Assert.AreEqual(TransientAssessmentExecutionStatus.Produced, outcome.Status, outcome.ReasonCode);
        Assert.IsNotNull(outcome.Descriptor);
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

    private sealed record Fixture(
        Linear16Frame Target,
        Linear16Frame Background,
        Linear16PixelMask HardMask,
        Linear16PixelMask SaturationMask,
        TransientCandidateExtractionRequest Request);

    private sealed record StageMeasurement(
        double MedianMilliseconds,
        double P95Milliseconds,
        double CpuMillisecondsPerOperation,
        double AllocatedBytesPerOperation);

    private sealed record Workload(
        string Id,
        int SourceWidth,
        int SourceHeight,
        CameraPixelFormat SourceFormat,
        int DetectorWidth,
        int DetectorHeight,
        string ExpectedOutputIdentitySha256)
    {
        public static Workload W1 { get; } = new(
            "W1", 1936, 1216, CameraPixelFormat.Mono16, 1936, 1216,
            "0F571F5488EF7881FF61D41F7212110F665FA9F678146DF03F16CE791859F8EE");
        public static Workload W2 { get; } = new(
            "W2", 3096, 2080, CameraPixelFormat.BayerRggb16, 1548, 1040,
            "8679D6D589E0DD706B91826B6F843CB368C7232A3359C9AE22B02453FECB2CCF");
        public int SourceBytes => checked(SourceWidth * SourceHeight * 2);
        public int DetectorBytes => checked(DetectorWidth * DetectorHeight * 2);
    }
}
