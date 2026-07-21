using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
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
    private const int TrialCount = 5;
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
        Assert.AreEqual(Architecture.X64, RuntimeInformation.OSArchitecture,
            "Transient performance evidence requires an x64 host.");
        Assert.AreEqual(Architecture.X64, RuntimeInformation.ProcessArchitecture,
            "Transient performance evidence requires an x64 process; run with --arch x64.");
        Assert.AreEqual("Release", GetType().Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
            "Transient performance evidence requires a Release assembly identity.");
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            AppContext.BaseDirectory,
            StringComparison.Ordinal,
            "Transient performance evidence must be collected from a Release build.");
        var results = new List<object>();
        foreach (var workload in new[] { Workload.W1, Workload.W2 })
        {
            var fixture = CreateFixture(workload);
            var trials = Enumerable.Range(1, TrialCount)
                .Select(trial => Measure(workload, fixture, trial))
                .ToArray();
            Assert.HasCount(1, trials.Select(static value => value.CompletePath.ExtractionReceiptIdentitySha256)
                .Distinct(StringComparer.Ordinal).ToArray(), $"{workload.Id} complete extraction receipts");
            Assert.HasCount(1, trials.Select(static value => value.CompletePath.AssessmentReceiptIdentitySha256)
                .Distinct(StringComparer.Ordinal).ToArray(), $"{workload.Id} complete assessment receipts");
            results.Add(new
            {
                workload.Id,
                workload.BaseWorkloadId,
                TrialCount,
                FixtureConstructionCount = 1,
                Trials = trials,
                AcrossTrialSummary = new
                {
                    DirectAlgorithm = Summarize(trials.Select(static value => value.DirectAlgorithm)),
                    ProcessingExtraction = Summarize(trials.Select(static value => value.Processing.CandidateExtraction)),
                    Assessment = Summarize(trials.Select(static value => value.Processing.Assessment)),
                    CompletePath = Summarize(trials.Select(static value => value.CompletePath.Stage))
                }
            });
        }

        var root = GetRepositoryRoot();
        var outputDirectory = Path.Combine(root, "TestResults", "issue-119");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "candidate-extraction-performance.json");
        var evidence = new
        {
            SchemaVersion = "issue-119-transient-detection-performance-v1",
            RecordedUtc = DateTimeOffset.UtcNow,
            BaselineRevision = "c68cf04c1d70701127764445d622eebabb0655eb",
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
            Command = "dotnet test tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj --configuration Release --arch x64 --filter FullyQualifiedName~TransientCandidateExtractionPerformanceTests.W1W2CandidateExtractionEvidence",
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
                TrialCount,
                ExposureDurationSeconds = 1,
                SourceCadenceSeconds = 5,
                TemporalContextDurationSeconds = 21,
                TimingGaps = "Five source starts are separated by five seconds; one-second exposures leave four-second inter-exposure gaps.",
                Concurrency = 1,
                InitialBacklog = 0
            },
            Method = "Five independent steady trials per workload. Each trial performs five warmups followed by 30 measured operations for the borrowed-buffer Imaging primitive, validated Processing extraction, deterministic assessment, and a complete source-artifact/input-conversion/background/extraction/promotion/assessment path. Per-trial p95 comes from those 30 operation samples; median/minimum/maximum summarize the five trial statistics and are not treated as p95.",
            WorkloadManifest = "Named W1-T119/W2-T119 full-size deterministic synthetic residuals use the canonical W1/W2 dimensions and formats. A replicated 2x2 RGGB source cell preserves the controlled W2 detector residual. The named derivative is required because canonical no-event sky frames do not provide a stable positive extraction/assessment workload; construction and source checksums are recorded per trial.",
            BufferOwnership = "Target/background are borrowed. Extraction owns state/traversal arrays, foreground-support arrays, bounded component/profile metadata, candidates, and a bounded receipt; no residual frame or retained full-frame output is created.",
            BaselineComparison = new
            {
                Disposition = "Net-new five-trial composite baseline over the merged #121 extraction implementation",
                BeforeValue = "N/A; issue #119 adds the first complete five-trial detector composition and changes no production algorithm.",
                NearestBaseline = "#115 temporal background evidence provides the nearest full-frame Processing/Imaging scan baseline.",
                Interpretation = "Absolute candidate latency/allocation baseline. Correctness and confusion are locked by the separate #119 oracle matrix; no production algorithm changed in #119."
            },
            Composition = new
            {
                DetectorStage = "This harness starts from resolved Mono16 detector buffers. W2 source dimensions and bytes describe the canonical Bayer source but are not scanned by this substage.",
                SourceConversionEvidence = "TestResults/issue-113/local/transient-contract-input-performance.json",
                TemporalWindowEvidence = "TestResults/issue-115/temporal-background-performance.json",
                Interpretation = "Review source conversion/window evidence together with these detector extraction and assessment measurements; costs are not represented as an end-to-end sum because retained windows overlap."
            },
            CostAttribution = new
            {
                Algorithm = "Measured separately and in the complete path: Linear16 extraction plus Processing validation and receipt creation. Assessment is separately measured without promotion and included with promotion in the complete path.",
                InputLoadingAndReconstruction = "The complete path measures in-memory source-artifact reconstruction and detector-input conversion. Issue #113 remains the detailed conversion-only baseline at TestResults/issue-113/local/transient-contract-input-performance.json.",
                TemporalWindow = "The complete path measures centered temporal background construction. Issue #115 remains the detailed causal/centered and persistent-mask baseline.",
                Persistence = "N/A; host-neutral issue #119 execution performs no filesystem, SQLite, SQL Server, MinIO, or network persistence. Zero observed I/O is not a zero-latency persistence claim."
            },
            AlgorithmicComplexity = "Source conversion is O(source pixels); centered background is O(context frames * detector pixels); extraction is O(detector pixels + bounded foreground/component work); deterministic assessment is O(observations and bounded profiles).",
            RuntimeSignals = "N/A; host-neutral extraction adds no queue, worker, health, logging, metric, trace, persistence, or external I/O boundary.",
            Results = results
        };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(evidence, EvidenceJson)).ConfigureAwait(false);
        TestContext.WriteLine($"Issue #119 performance evidence: {outputPath}");
    }

    [TestMethod]
    public async Task Issue116W2CandidateExtractionRecordsAcceptanceEvidence()
    {
        await W1W2CandidateExtractionEvidence().ConfigureAwait(false);

        var root = GetRepositoryRoot();
        var sourcePath = Path.Combine(root, "TestResults", "issue-119", "candidate-extraction-performance.json");
        using var source = JsonDocument.Parse(await File.ReadAllTextAsync(sourcePath).ConfigureAwait(false));
        var w2 = source.RootElement.GetProperty("results").EnumerateArray()
            .Single(result => result.GetProperty("id").GetString()!.StartsWith("W2", StringComparison.Ordinal))
            .Clone();
        var revision = ReadGit(root, "rev-parse HEAD");
        var dirtyState = ReadGit(root, "status --short");
        var evidenceRevision = string.IsNullOrWhiteSpace(dirtyState) ? revision : "local-dirty";
        var boundedRevision = evidenceRevision.Length > 12 ? evidenceRevision[..12] : evidenceRevision;
        var outputDirectory = Path.Combine(root, "TestResults", "issue-116", boundedRevision);
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "transient-detector-w2.json");
        var evidence = new
        {
            Schema = "hvo-central-transient-detector-component-v1",
            Issue = 116,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            Revision = new
            {
                Candidate = revision,
                Branch = ReadGit(root, "branch --show-current"),
                Dirty = !string.IsNullOrWhiteSpace(dirtyState),
                DirtyState = dirtyState,
                DirtyFingerprintSha256 = CreateDirtyFingerprint(root)
            },
            Command = "dotnet test tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj --configuration Release --arch x64 --filter FullyQualifiedName~TransientCandidateExtractionPerformanceTests.Issue116W2CandidateExtractionRecordsAcceptanceEvidence",
            Boundary = "Isolated host-neutral W2 source conversion, centered temporal background, candidate extraction, promotion, and deterministic assessment. Central SQL/MinIO/window metrics are in central-w2-w3m-w4.json from the same revision.",
            SourceEvidence = new
            {
                Path = "TestResults/issue-119/candidate-extraction-performance.json",
                Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath).ConfigureAwait(false)))
            },
            TestedAssemblies = new[]
            {
                CreateAssemblyEvidence(GetType().Assembly),
                CreateAssemblyEvidence(typeof(TransientCandidateExtractionFactory).Assembly)
            },
            FixtureDisclosure = "W2-T119 is a deterministic synthetic replicated detector residual shaped to canonical W2 dimensions and RGGB bytes. It is not a VirtualSky render, production capture, or physical sensitivity claim.",
            Workload = w2,
            Interpretation = "The W2 component reports phase median/p95, CPU, allocations, LOH/RSS, live and retained buffers, throughput, candidate rate, exact source/output identities, and zero host I/O. It is isolated so process-wide resource counters are not contaminated by SQL Server or MinIO fixture work."
        };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(evidence, EvidenceJson)).ConfigureAwait(false);
        TestContext.WriteLine($"Issue #116 W2 detector evidence: {outputPath}");
    }

    public TestContext TestContext { get; set; } = null!;

    private static TrialMeasurement Measure(Workload workload, Fixture fixture, int trial)
    {
        for (var index = 0; index < WarmupCount; index++)
        {
            AssertResult(Linear16TransientExtraction.Extract(
                fixture.Target, fixture.Background, fixture.HardMask, fixture.SaturationMask, Options));
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var gcStart = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetStart = process.WorkingSet64;
        var liveStart = GC.GetTotalMemory(false);
        var durations = new double[MeasurementCount];
        var cpuDurations = new double[MeasurementCount];
        var allocationStart = GC.GetTotalAllocatedBytes(true);
        var peakWorkingSet = workingSetStart;
        var gen0Start = GC.CollectionCount(0);
        var gen1Start = GC.CollectionCount(1);
        var gen2Start = GC.CollectionCount(2);
        for (var index = 0; index < durations.Length; index++)
        {
            process.Refresh();
            var cpuStarted = process.TotalProcessorTime;
            var started = Stopwatch.GetTimestamp();
            var result = Linear16TransientExtraction.Extract(
                fixture.Target, fixture.Background, fixture.HardMask, fixture.SaturationMask, Options);
            durations[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            process.Refresh();
            cpuDurations[index] = (process.TotalProcessorTime - cpuStarted).TotalMilliseconds;
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            AssertResult(result);
        }
        var allocated = GC.GetTotalAllocatedBytes(false) - allocationStart;
        Array.Sort(durations);
        Array.Sort(cpuDurations);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var gcEnd = GC.GetGCMemoryInfo();
        process.Refresh();
        var workingSetEnd = process.WorkingSet64;
        var liveEnd = GC.GetTotalMemory(false);
        var final = Linear16TransientExtraction.Extract(
            fixture.Target, fixture.Background, fixture.HardMask, fixture.SaturationMask, Options);
        AssertResult(final);
        var median = durations[durations.Length / 2];
        var p95 = durations[(int)Math.Ceiling(durations.Length * 0.95) - 1];
        var directMeasurement = new StageMeasurement(
            median,
            p95,
            cpuDurations[cpuDurations.Length / 2],
            cpuDurations[(int)Math.Ceiling(cpuDurations.Length * 0.95) - 1],
            cpuDurations.Average(),
            allocated / (double)MeasurementCount,
            MeasurementCount / (durations.Sum() / 1000),
            workingSetStart,
            workingSetEnd,
            peakWorkingSet,
            liveStart,
            liveEnd,
            gcStart.GenerationInfo[3].SizeAfterBytes,
            gcEnd.GenerationInfo[3].SizeAfterBytes);
        var allocatedPerOperation = allocated / (double)MeasurementCount;
        var detectorPixels = (long)workload.DetectorWidth * workload.DetectorHeight;
        Assert.IsLessThan(1_250, p95, $"{workload.Id} extraction p95 exceeded 25% of five-second cadence.");
        Assert.IsLessThan(detectorPixels * 6 + 2_000_000, allocatedPerOperation,
            $"{workload.Id} allocations exceeded the declared state/queue plus metadata boundary.");
        var outputBytes = JsonSerializer.SerializeToUtf8Bytes(final.Components).Length;
        var geometryBytes = JsonSerializer.SerializeToUtf8Bytes(final.Components.Select(static component => new
        {
            component.BoundsX,
            component.BoundsY,
            component.BoundsWidth,
            component.BoundsHeight,
            component.StartX,
            component.StartY,
            component.EndX,
            component.EndY
        })).Length;
        var outputIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(final.Components);
        Assert.AreEqual(workload.ExpectedOutputIdentitySha256, outputIdentity, workload.Id);
        var processing = MeasureProcessing(fixture.Request, workload.Id);
        var completePath = MeasureCompletePath(workload, fixture);

        var detail = new
        {
            Trial = trial,
            workload.Id,
            Source = new { workload.SourceWidth, workload.SourceHeight, PixelFormat = workload.SourceFormat.ToString(), workload.SourceBytes },
            Detector = new
            {
                Width = workload.DetectorWidth,
                Height = workload.DetectorHeight,
                BytesPerFrame = workload.DetectorBytes,
                BorrowedFrameCount = 2,
                BorrowedBytes = workload.DetectorBytes * 2L,
                LogicalRetainedWindowFrames = 5,
                LogicalRetainedWindowBytes = workload.DetectorBytes * 5L
            },
            CompletePathSource = new
            {
                ActualFixtureOwnedBytes = fixture.SourceTarget.Length + (long)fixture.SourceBackground.Length,
                LogicalFiveSourceBytes = workload.SourceBytes * 5L,
                TargetSha256 = Convert.ToHexString(SHA256.HashData(fixture.SourceTarget)),
                BackgroundSha256 = Convert.ToHexString(SHA256.HashData(fixture.SourceBackground)),
                FullFrameCopyAccounting = workload.SourceFormat == CameraPixelFormat.Mono16
                    ? "Two unique borrowed source buffers back five temporal positions; input conversion makes no source-sized copy. One detector-sized background and bounded extraction state/traversal arrays are owned per operation."
                    : "Two unique RGGB source buffers back five temporal positions. Conversion owns five detector-sized Mono16 arrays; centered background owns one detector-sized output. Extraction owns bounded detector state/traversal arrays."
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
                GeometryBytes = geometryBytes,
                StructuredComponentBytes = outputBytes,
                OverlayBytes = "N/A; V1 emits authoritative geometry and overlay derivatives are outside issue #121.",
                IdentitySha256 = outputIdentity,
                final.ForegroundPixelCount,
                final.HardMaskedPixelCount,
                final.SaturatedPixelCount
            },
            Latency = new { MedianMilliseconds = median, P95Milliseconds = p95 },
            Cpu = new
            {
                directMeasurement.MedianCpuMilliseconds,
                directMeasurement.P95CpuMilliseconds,
                directMeasurement.MeanCpuMilliseconds,
                Sampling = "Process.TotalProcessorTime delta per measured operation; process-wide and timer-quantized."
            },
            AllocatedBytesPerOperation = allocatedPerOperation,
            AllocationRateBytesPerSecond = allocatedPerOperation / (median / 1000),
            ThroughputFramesPerSecond = MeasurementCount / (durations.Sum() / 1000),
            ThroughputDetectorMiBPerSecond = final.BytesScanned / 1024d / 1024d / (median / 1000),
            CostMillisecondsPerElapsedInputSecond = median / 5,
            Memory = new
            {
                WorkingSetStartBytes = workingSetStart,
                WorkingSetEndBytes = workingSetEnd,
                MaximumObservedPostOperationWorkingSetBytes = peakWorkingSet,
                ManagedLiveStartBytes = liveStart,
                ManagedLiveEndBytes = liveEnd,
                RetainedDeltaBytes = liveEnd - liveStart,
                TemporaryStateBytes = detectorPixels,
                TemporaryResidualBytes = 0,
                TemporaryTraversalBytes = detectorPixels * sizeof(int),
                LohArraysPerOperation = 2,
                LogicalRetainedWindowBytes = workload.DetectorBytes * 5L,
                LohSizeStartAfterGcBytes = gcStart.GenerationInfo[3].SizeAfterBytes,
                LohSizeEndAfterGcBytes = gcEnd.GenerationInfo[3].SizeAfterBytes,
                LohFragmentationStartAfterGcBytes = gcStart.GenerationInfo[3].FragmentationAfterBytes,
                LohFragmentationEndAfterGcBytes = gcEnd.GenerationInfo[3].FragmentationAfterBytes
            },
            Collections = new
            {
                Gen0 = GC.CollectionCount(0) - gen0Start,
                Gen1 = GC.CollectionCount(1) - gen1Start,
                Gen2 = GC.CollectionCount(2) - gen2Start
            },
            Processing = processing,
            CompletePath = completePath,
            Backlog = new { Count = 0, Bytes = 0, OldestAgeSeconds = 0, DrainRate = "N/A; synchronous pure algorithm" }
        };
        return new TrialMeasurement(trial, directMeasurement, processing, completePath, detail);
    }

    private static ProcessingMeasurement MeasureProcessing(TransientCandidateExtractionRequest request, string workloadId)
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
        return new ProcessingMeasurement(
            extractionMeasurement,
            extractionReceipt.Length,
            Convert.ToHexString(SHA256.HashData(extractionReceipt)),
            assessmentMeasurement,
            assessmentReceipt.Length,
            Convert.ToHexString(SHA256.HashData(assessmentReceipt)));
    }

    private static CompletePathMeasurement MeasureCompletePath(Workload workload, Fixture fixture)
    {
        for (var index = 0; index < WarmupCount; index++)
        {
            AssertCompletePath(ExecuteCompletePath(workload, fixture));
        }
        var stage = MeasureStage(() => AssertCompletePath(ExecuteCompletePath(workload, fixture)));
        Assert.IsLessThan(1_250, stage.P95Milliseconds,
            $"{workload.Id} complete detector p95 exceeded 25% of five-second cadence.");
        var final = ExecuteCompletePath(workload, fixture);
        AssertCompletePath(final);
        var extractionReceipt = TransientCandidateExtractionJson.Serialize(final.Extraction.Descriptor!);
        var assessmentReceipt = TransientAssessmentJson.Serialize(final.Assessment.Descriptor!);
        return new CompletePathMeasurement(
            stage,
            Convert.ToHexString(SHA256.HashData(extractionReceipt)),
            Convert.ToHexString(SHA256.HashData(assessmentReceipt)),
            final.Extraction.Candidates.Count,
            stage.MedianMilliseconds / 5,
            stage.ThroughputOperationsPerSecond);
    }

    private static CompletePathOutcome ExecuteCompletePath(Workload workload, Fixture fixture)
    {
        var request = CreateProcessingRequest(
            workload,
            fixture.SourceTarget,
            fixture.SourceBackground,
            sourceResolution: true);
        var extraction = TransientCandidateExtractionFactory.Create(request);
        AssertExtraction(extraction);
        var observation = TransientObservationFactory.CreateAssessmentObservation(new TransientObservationPromotionRequest(
            extraction.Candidates[0].CandidateId,
            Guid.Parse("f1000000-0000-0000-0000-000000000001"),
            0,
            extraction.Descriptor!));
        var assessment = TransientAssessmentFactory.Create(new TransientAssessmentExecutionRequest(
            extraction.Candidates[0].EventId,
            Guid.Parse("f2000000-0000-0000-0000-000000000001"),
            request.CreatedUtc.AddSeconds(1),
            TransientAssessmentAuthority.Provisional,
            [observation],
            AssessmentOptions,
            []));
        return new CompletePathOutcome(extraction, assessment);
    }

    private static void AssertCompletePath(CompletePathOutcome outcome)
    {
        AssertExtraction(outcome.Extraction);
        AssertAssessment(outcome.Assessment);
    }

    private static StageMeasurement MeasureStage(Action action)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var gcStart = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetStart = process.WorkingSet64;
        var peakWorkingSet = workingSetStart;
        var liveStart = GC.GetTotalMemory(false);
        var durations = new double[MeasurementCount];
        var cpuDurations = new double[MeasurementCount];
        var allocationStart = GC.GetTotalAllocatedBytes(true);
        for (var index = 0; index < durations.Length; index++)
        {
            process.Refresh();
            var cpuStarted = process.TotalProcessorTime;
            var started = Stopwatch.GetTimestamp();
            action();
            durations[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            process.Refresh();
            cpuDurations[index] = (process.TotalProcessorTime - cpuStarted).TotalMilliseconds;
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        }
        var allocated = GC.GetTotalAllocatedBytes(false) - allocationStart;
        Array.Sort(durations);
        Array.Sort(cpuDurations);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var gcEnd = GC.GetGCMemoryInfo();
        process.Refresh();
        return new StageMeasurement(
            durations[durations.Length / 2],
            durations[(int)Math.Ceiling(durations.Length * 0.95) - 1],
            cpuDurations[cpuDurations.Length / 2],
            cpuDurations[(int)Math.Ceiling(cpuDurations.Length * 0.95) - 1],
            cpuDurations.Average(),
            allocated / (double)MeasurementCount,
            MeasurementCount / (durations.Sum() / 1000),
            workingSetStart,
            process.WorkingSet64,
            peakWorkingSet,
            liveStart,
            GC.GetTotalMemory(false),
            gcStart.GenerationInfo[3].SizeAfterBytes,
            gcEnd.GenerationInfo[3].SizeAfterBytes);
    }

    private static StageSummary Summarize(IEnumerable<StageMeasurement> values)
    {
        var samples = values.ToArray();
        return new StageSummary(
            Distribution.Create(samples.Select(static value => value.MedianMilliseconds)),
            Distribution.Create(samples.Select(static value => value.P95Milliseconds)),
            Distribution.Create(samples.Select(static value => value.MedianCpuMilliseconds)),
            Distribution.Create(samples.Select(static value => value.P95CpuMilliseconds)),
            Distribution.Create(samples.Select(static value => value.AllocatedBytesPerOperation)),
            Distribution.Create(samples.Select(static value => value.ThroughputOperationsPerSecond)));
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
        var sourceTarget = workload.SourceFormat == CameraPixelFormat.Mono16
            ? targetBytes
            : ExpandDetectorToRggb(workload, targetBytes);
        var sourceBackground = workload.SourceFormat == CameraPixelFormat.Mono16
            ? backgroundBytes
            : ExpandDetectorToRggb(workload, backgroundBytes);
        return new Fixture(
            new Linear16Frame(workload.DetectorWidth, workload.DetectorHeight, stride, CameraPixelFormat.Mono16, targetBytes),
            new Linear16Frame(workload.DetectorWidth, workload.DetectorHeight, stride, CameraPixelFormat.Mono16, backgroundBytes),
            Linear16MaskOperations.Empty(workload.DetectorWidth, workload.DetectorHeight),
            Linear16MaskOperations.Empty(workload.DetectorWidth, workload.DetectorHeight),
            request,
            sourceTarget,
            sourceBackground);
    }

    private static TransientCandidateExtractionRequest CreateProcessingRequest(
        Workload workload,
        byte[] targetBytes,
        byte[] backgroundBytes,
        bool sourceResolution = false)
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
                position == TransientTemporalPosition.N ? targetBytes : backgroundBytes,
                sourceResolution));
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
        byte[] payload,
        bool sourceResolution)
    {
        var sequence = 100 + (int)position;
        var started = TransientTestData.Epoch.AddSeconds((int)position * 5 + 20);
        var ended = started.AddSeconds(1);
        var artifactId = Guid.Parse($"f5000000-0000-0000-0000-{sequence:D12}");
        var evidenceId = Guid.Parse($"f6000000-0000-0000-0000-{sequence:D12}");
        var template = TransientTestData.CreateDetectorSource(CameraPixelFormat.Mono16);
        var width = sourceResolution ? workload.SourceWidth : workload.DetectorWidth;
        var height = sourceResolution ? workload.SourceHeight : workload.DetectorHeight;
        var pixelFormat = sourceResolution ? workload.SourceFormat : CameraPixelFormat.Mono16;
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
                Width = width,
                Height = height,
                StrideBytes = width * 2,
                PixelFormat = pixelFormat,
                CfaPattern = pixelFormat == CameraPixelFormat.BayerRggb16
                    ? ColorFilterArrayPattern.Rggb
                    : ColorFilterArrayPattern.None,
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
        var detectorWidth = input.Input!.Descriptor.Layout.Width;
        var detectorHeight = input.Input.Descriptor.Layout.Height;
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
            Linear16MaskOperations.Empty(detectorWidth, detectorHeight))).ToArray();
        return new TransientTemporalSource(
            position,
            sequence,
            input.Input!,
            new TransientSensitivityV1("performance-response-v1", 1, 1),
            masks);
    }

    private static byte[] ExpandDetectorToRggb(Workload workload, byte[] detector)
    {
        var source = new byte[workload.SourceBytes];
        for (var y = 0; y < workload.DetectorHeight; y++)
        {
            for (var x = 0; x < workload.DetectorWidth; x++)
            {
                var detectorOffset = (y * workload.DetectorWidth + x) * 2;
                for (var sourceY = y * 2; sourceY < y * 2 + 2; sourceY++)
                {
                    for (var sourceX = x * 2; sourceX < x * 2 + 2; sourceX++)
                    {
                        var sourceOffset = (sourceY * workload.SourceWidth + sourceX) * 2;
                        source[sourceOffset] = detector[detectorOffset];
                        source[sourceOffset + 1] = detector[detectorOffset + 1];
                    }
                }
            }
        }
        return source;
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

    private static string CreateDirtyFingerprint(string root)
    {
        var value = new StringBuilder(ReadGit(root, "diff --binary HEAD"));
        var untracked = ReadGit(root, "ls-files --others --exclude-standard -z")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal);
        foreach (var relativePath in untracked)
        {
            value.Append('\n').Append(relativePath).Append(':')
                .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relativePath)))));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }

    private static AssemblyEvidence CreateAssemblyEvidence(Assembly assembly)
        => new(
            assembly.GetName().Name ?? "unknown",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            assembly.ManifestModule.ModuleVersionId,
            assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown");

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
        TransientCandidateExtractionRequest Request,
        byte[] SourceTarget,
        byte[] SourceBackground);

    private sealed record AssemblyEvidence(
        string Name,
        string Sha256,
        Guid ModuleVersionId,
        string Configuration);

    private sealed record StageMeasurement(
        double MedianMilliseconds,
        double P95Milliseconds,
        double MedianCpuMilliseconds,
        double P95CpuMilliseconds,
        double MeanCpuMilliseconds,
        double AllocatedBytesPerOperation,
        double ThroughputOperationsPerSecond,
        long WorkingSetStartBytes,
        long WorkingSetEndBytes,
        long MaximumObservedPostOperationWorkingSetBytes,
        long ManagedLiveStartBytes,
        long ManagedLiveEndBytes,
        long LohSizeStartAfterGcBytes,
        long LohSizeEndAfterGcBytes);

    private sealed record ProcessingMeasurement(
        StageMeasurement CandidateExtraction,
        int ExtractionReceiptBytes,
        string ExtractionReceiptIdentitySha256,
        StageMeasurement Assessment,
        int AssessmentReceiptBytes,
        string AssessmentReceiptIdentitySha256);

    private sealed record TrialMeasurement(
        int Trial,
        StageMeasurement DirectAlgorithm,
        ProcessingMeasurement Processing,
        CompletePathMeasurement CompletePath,
        object Detail);

    private sealed record CompletePathMeasurement(
        StageMeasurement Stage,
        string ExtractionReceiptIdentitySha256,
        string AssessmentReceiptIdentitySha256,
        int CandidateCount,
        double CostMillisecondsPerElapsedInputSecond,
        double FramesPerSecond);

    private sealed record CompletePathOutcome(
        TransientCandidateExtractionOutcome Extraction,
        TransientAssessmentExecutionOutcome Assessment);

    private sealed record StageSummary(
        Distribution MedianWallMilliseconds,
        Distribution P95WallMilliseconds,
        Distribution MedianCpuMilliseconds,
        Distribution P95CpuMilliseconds,
        Distribution AllocatedBytesPerOperation,
        Distribution ThroughputOperationsPerSecond);

    private sealed record Distribution(int Count, double Median, double Minimum, double Maximum)
    {
        public static Distribution Create(IEnumerable<double> values)
        {
            var ordered = values.Order().ToArray();
            return new Distribution(ordered.Length, ordered[ordered.Length / 2], ordered[0], ordered[^1]);
        }
    }

    private sealed record Workload(
        string Id,
        string BaseWorkloadId,
        int SourceWidth,
        int SourceHeight,
        CameraPixelFormat SourceFormat,
        int DetectorWidth,
        int DetectorHeight,
        string ExpectedOutputIdentitySha256)
    {
        public static Workload W1 { get; } = new(
            "W1-T119", "W1", 1936, 1216, CameraPixelFormat.Mono16, 1936, 1216,
            "0F571F5488EF7881FF61D41F7212110F665FA9F678146DF03F16CE791859F8EE");
        public static Workload W2 { get; } = new(
            "W2-T119", "W2", 3096, 2080, CameraPixelFormat.BayerRggb16, 1548, 1040,
            "8679D6D589E0DD706B91826B6F843CB368C7232A3359C9AE22B02453FECB2CCF");
        public int SourceBytes => checked(SourceWidth * SourceHeight * 2);
        public int DetectorBytes => checked(DetectorWidth * DetectorHeight * 2);
    }
}
