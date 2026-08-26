using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires a public test class.")]
public sealed class Issue433LayeredPresentationPerformanceTests
{
    internal const int WarmupCount = 5;
    internal const int MeasuredCount = 30;
    internal const int Width = 3552;
    internal const int Height = 3552;
    internal const int FrameBytes = Width * Height;
    internal const int ObjectCount = 2;
    internal const int SegmentCount = 1;
    private const string ResultSchema = "issue-433-layered-presentation-performance-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] SourcePathSpecs =
    [
        "src/HVO.SkyMonitor.AgentCore",
        "src/HVO.SkyMonitor.Astronomy",
        "src/HVO.SkyMonitor.Imaging",
        "src/HVO.SkyMonitor.Processing",
        "tests/HVO.SkyMonitor.Imaging.Tests",
        "tests/HVO.SkyMonitor.Processing.Tests",
        "scripts/evidence:issue-433",
        "docs/validation/issue-433-runtime-signals.json",
        "Directory.Build.props",
        "Directory.Packages.props",
        "global.json",
        "HVO.SkyMonitor.v9.slnx"
    ];
    private static readonly string[] LegacySourcePaths =
    [
        "src/HVO.SkyMonitor.Imaging/AnnotationRenderer.cs",
        "src/HVO.SkyMonitor.Imaging/WeatherCloudOverlayRenderer.cs",
        "src/HVO.SkyMonitor.Processing/BuiltInProcessingRecipes.cs",
        "src/HVO.SkyMonitor.Processing/WeatherCloudOverlayRecipe.cs",
        "src/HVO.SkyMonitor.Imaging/PresentationLayerPayload.cs"
    ];
    private static readonly string[] EvidenceOnlyPaths =
    [
        "docs/validation/issue-433-runtime-signals.json",
        "scripts/evidence:issue-433",
        "tests/HVO.SkyMonitor.Processing.Tests/Issue433LayeredPresentationPerformanceTests.cs"
    ];

    [TestMethod]
    public void FullResolutionSyntheticMono8OldAndLayeredPathsEvidence()
    {
        if (Environment.GetEnvironmentVariable("HVO_ISSUE433_EVIDENCE") != "1")
            Assert.Inconclusive("Set HVO_ISSUE433_EVIDENCE=1 for an explicit issue #433 evidence run.");
        Assert.AreEqual("Release", BuildConfiguration);

        var root = RepositoryRoot();
        var receiptPath = Environment.GetEnvironmentVariable("HVO_ISSUE433_BUILD_RECEIPT");
        Assert.IsFalse(string.IsNullOrWhiteSpace(receiptPath));
        var receipt = VerifyReceipt(root, receiptPath!);
        var runtimeBefore = RuntimeInventory(Path.GetDirectoryName(typeof(Issue433LayeredPresentationPerformanceTests).Assembly.Location)!);
        Assert.AreEqual(receipt.RuntimeOutputSetSha256, runtimeBefore.Sha256, ignoreCase: true);
        Assert.AreEqual(receipt.RuntimeOutputFileCount, runtimeBefore.FileCount);
        var receiptSha256 = Sha256(File.ReadAllBytes(receiptPath!));

        var fixture = CreateFixture();
        var baseChecksum = Sha256(fixture.BasePixels);
        string? expectedChecksum = null;
        for (var index = 0; index < WarmupCount; index++)
        {
            var old = ConsumeOutput(() => RunOld(fixture));
            var layered = ConsumeOutput(() => RunLayered(fixture));
            Assert.AreEqual(old, layered);
            expectedChecksum ??= old.ChecksumSha256;
            Assert.AreEqual(expectedChecksum, old.ChecksumSha256);
            Assert.AreEqual(expectedChecksum, layered.ChecksumSha256);
            Assert.AreEqual(baseChecksum, Sha256(fixture.BasePixels));
        }

        using var process = Process.GetCurrentProcess();
        var oldAnnotation = new List<BoundaryMeasurement>(MeasuredCount);
        var oldWeather = new List<BoundaryMeasurement>(MeasuredCount);
        var oldEndToEnd = new List<BoundaryMeasurement>(MeasuredCount);
        var payloadConstruction = new List<BoundaryMeasurement>(MeasuredCount);
        var materialization = new List<BoundaryMeasurement>(MeasuredCount);
        var layeredEndToEnd = new List<BoundaryMeasurement>(MeasuredCount);
        var structuredBytes = new long[MeasuredCount];
        var manifestBytes = new long[MeasuredCount];
        var oldMemoryEndpoints = new List<GroupMemoryEndpoint>(MeasuredCount);
        var layeredMemoryEndpoints = new List<GroupMemoryEndpoint>(MeasuredCount);
        var memoryCursor = CollectMemoryEndpoint(process);

        for (var iteration = 0; iteration < MeasuredCount; iteration++)
        {
            OutputIdentity oldIdentity;
            OutputIdentity layeredIdentity;
            if (iteration % 2 == 0)
            {
                oldIdentity = MeasureOldGroup(fixture, process, oldAnnotation, oldWeather, oldEndToEnd,
                    oldMemoryEndpoints, ref memoryCursor);
                layeredIdentity = MeasureLayeredGroup(fixture, process, payloadConstruction, materialization,
                    layeredEndToEnd, layeredMemoryEndpoints, ref memoryCursor,
                    out structuredBytes[iteration], out manifestBytes[iteration]);
            }
            else
            {
                layeredIdentity = MeasureLayeredGroup(fixture, process, payloadConstruction, materialization,
                    layeredEndToEnd, layeredMemoryEndpoints, ref memoryCursor,
                    out structuredBytes[iteration], out manifestBytes[iteration]);
                oldIdentity = MeasureOldGroup(fixture, process, oldAnnotation, oldWeather, oldEndToEnd,
                    oldMemoryEndpoints, ref memoryCursor);
            }

            Assert.AreEqual(FrameBytes, oldIdentity.ByteLength);
            Assert.AreEqual(oldIdentity.ByteLength, layeredIdentity.ByteLength);
            Assert.AreEqual(expectedChecksum, oldIdentity.ChecksumSha256);
            Assert.AreEqual(expectedChecksum, layeredIdentity.ChecksumSha256);
            Assert.AreEqual(baseChecksum, Sha256(fixture.BasePixels), "The borrowed base frame changed.");
        }

        var oldSummary = Summary(oldEndToEnd);
        var layeredSummary = Summary(layeredEndToEnd);
        var changes = new Dictionary<string, PercentComparison>(StringComparer.Ordinal)
        {
            ["medianWallPercent"] = PercentChange(oldSummary.MedianWallMilliseconds, layeredSummary.MedianWallMilliseconds),
            ["p95WallPercent"] = PercentChange(oldSummary.P95WallMilliseconds, layeredSummary.P95WallMilliseconds),
            ["allocatedBytesPercent"] = PercentChange(oldSummary.AllocatedBytesPerOperation, layeredSummary.AllocatedBytesPerOperation),
            ["cpuMillisecondsPercent"] = PercentChange(oldSummary.CpuMillisecondsPerOperation, layeredSummary.CpuMillisecondsPerOperation),
            ["operationsPerSecondPercent"] = PercentChange(oldSummary.OperationsPerSecond, layeredSummary.OperationsPerSecond)
        };
        var materialFlags = MaterialFlags(changes);
        var outputRoot = Environment.GetEnvironmentVariable("HVO_ISSUE433_EVIDENCE_ROOT");
        var trial = Environment.GetEnvironmentVariable("HVO_EVIDENCE_TRIAL");
        Assert.IsFalse(string.IsNullOrWhiteSpace(outputRoot));
        Assert.AreEqual(receipt.Trial, trial);
        var outputDirectory = Path.GetFullPath(Path.Combine(outputRoot!, receipt.EvidenceHead, "trials", receipt.Trial));
        var outputPath = Path.Combine(outputDirectory, "issue-433-layered-presentation-performance.json");
        Assert.IsFalse(File.Exists(outputPath), "Evidence result is create-only.");

        var evidence = new
        {
            schemaVersion = ResultSchema,
            issue = 433,
            provenance = new
            {
                receipt.EvidenceHead,
                receipt.EvidenceHeadTree,
                receipt.CandidateProductCommit,
                receipt.CandidateProductTree,
                baselineRevision = receipt.BaselineCommit,
                receipt.BaselineCommit,
                receipt.BaselineTree,
                receipt.BaselineToCandidateProductDiffSha256,
                receipt.CandidateToEvidenceHarnessDiffSha256,
                receipt.MeasuredSourceInventorySha256,
                receipt.MeasuredSourceInventoryFileCount,
                receipt.RuntimeOutputSetSha256,
                receipt.RuntimeOutputFileCount,
                receipt.LegacyBlobProofs,
                buildReceiptPath = Path.GetFullPath(receiptPath!),
                buildReceiptSha256 = receiptSha256
            },
            environment = new
            {
                operatingSystem = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                runtime = RuntimeInformation.FrameworkDescription,
                gc = new { server = GCSettings.IsServerGC, latencyMode = GCSettings.LatencyMode.ToString() },
                processorCount = Environment.ProcessorCount,
                configuration = BuildConfiguration,
                concurrency = 1
            },
            workload = new
            {
                id = "synthetic-3552x3552-mono8-old-and-layered",
                canonicalW6 = false,
                relationshipToCanonicalW6 = "Complements unavailable external canonical W6 evidence; this deterministic synthetic fixture is not a canonical W6 capture or workload.",
                differencesFromCanonicalW6 = "Synthetic zero-valued Mono8 base of 12,616,704 bytes with exactly two projected objects, one segment, one image-circle projection, four metadata corners including environment text, and a deterministic 16x16 cloud mask.",
                width = Width,
                height = Height,
                pixelFormat = CameraPixelFormat.Mono8.ToString(),
                baseBytes = FrameBytes,
                deterministicBaseChecksumSha256 = baseChecksum,
                fixture.ObjectCount,
                fixture.SegmentCount,
                cloudGrid = "16x16",
                metadataCornerCount = 4,
                environmentMetadata = true,
                projectionGeometry = "image-circle",
                warmupCount = WarmupCount,
                measuredCount = MeasuredCount,
                alternatingOrder = true
            },
            boundaries = new
            {
                oldAnnotation = Summary(oldAnnotation),
                oldWeather = Summary(oldWeather),
                oldEndToEnd = oldSummary,
                newPayloadConstructionAndSerialization = Summary(payloadConstruction),
                newMaterialization = Summary(materialization),
                newEndToEnd = layeredSummary
            },
            memory = new
            {
                qualification = "A compacting full collection establishes each path-group boundary after the prior group's local outputs are hashed and released; that qualified endpoint is both the prior post-group and next pre-group sample, avoiding duplicate collections. Current-thread allocations are measured per synchronous boundary. CPU and RSS are process-wide. LOH uses GC generation-info index 3 and is an endpoint signal, not per-operation attribution.",
                legacyPathInCandidateBinary = MemorySummary(oldMemoryEndpoints),
                layeredPathInCandidateBinary = MemorySummary(layeredMemoryEndpoints)
            },
            artifactBytes = new
            {
                definition = "Bytes in products that the compared graph path would publish; excludes temporary canonical-identity JSON and other transient implementation allocations.",
                legacyPathInCandidateBinary = new
                {
                    annotationIntermediateBytesPerOperation = (long)FrameBytes,
                    weatherFinalBytesPerOperation = (long)FrameBytes,
                    structuredLayerPayloadBytesPerOperation = 0L,
                    manifestBytesPerOperation = 0L,
                    finalMaterializationBytesPerOperation = 0L,
                    totalPublishedArtifactBytesPerOperation = 2L * FrameBytes
                },
                layeredPathInCandidateBinary = new
                {
                    annotationIntermediateBytesPerOperation = 0L,
                    weatherFinalBytesPerOperation = 0L,
                    structuredLayerPayloadBytesPerOperation = structuredBytes.Average(),
                    manifestBytesPerOperation = manifestBytes.Average(),
                    finalMaterializationBytesPerOperation = (long)FrameBytes,
                    totalPublishedArtifactBytesPerOperation = structuredBytes.Average() + manifestBytes.Average() + FrameBytes
                }
            },
            transientMemory = new
            {
                definition = "Source-derived known owned full-frame byte buffers allocated by the actual end-to-end APIs; excludes the borrowed base and separately reports measured current-thread allocations in boundaries.",
                legacyPathInCandidateBinary = new
                {
                    knownFullFrameAllocationsPerOperation = 4,
                    transientKnownFullFrameBytesPerOperation = 4L * FrameBytes,
                    maximumSimultaneousOwnedFullFrames = 3,
                    maximumSimultaneousOwnedFullFrameBytes = 3L * FrameBytes,
                    qualification = "AnnotationRenderer allocates three successive packed byte frames and WeatherCloudOverlayRenderer allocates one packed byte frame. The segment bool mask and all non-frame allocations remain represented only by measured current-thread allocation bytes."
                },
                layeredPathInCandidateBinary = new
                {
                    knownFullFrameAllocationsPerOperation = 1,
                    transientKnownFullFrameBytesPerOperation = (long)FrameBytes,
                    maximumSimultaneousOwnedFullFrames = 1,
                    maximumSimultaneousOwnedFullFrameBytes = (long)FrameBytes,
                    qualification = "PresentationLayerCompositor clones the borrowed packed base exactly once; structured objects and serialized product bytes are not full-frame buffers."
                }
            },
            correctness = new
            {
                warmupOutputLengthAndSha256Equality = true,
                measuredIterationOutputLengthAndSha256Equality = true,
                deterministicChecksumSha256 = expectedChecksum,
                baseUnchangedEveryIteration = true,
                outputsHashedAndReleasedBeforeNextOperation = true
            },
            comparison = new
            {
                percentChangeDefinition = "When old and layered are finite and old is non-zero: (layered - old) / old * 100. Otherwise percent is null with an explicit notApplicableReason. Negative is lower except operations/second, where positive is higher.",
                changes,
                materialThresholdPercent = 10,
                materialFlags,
                disposition = materialFlags.Length == 0
                    ? "No material greater-than-10-percent comparison signal requires disposition."
                    : "Material greater-than-10-percent signals are flagged for operator disposition; this harness applies no arbitrary performance failure threshold.",
                baselineRevisionRole = "ancestry-and-source-equivalence-only",
                measuredBinary = "The measured candidate binary includes the evidence-only harness commit and is built from evidenceHead. Production source is pinned to candidateProductCommit; the commits after it are restricted to the three allowlisted evidence files. legacyPathInCandidateBinary and layeredPathInCandidateBinary execute in that same build. No separate baseline build was measured.",
                interpretation = "Correctness is the only fail gate. Timing, CPU, allocation, throughput, RSS, and retained-LOH changes are comparison evidence and require explanation rather than an arbitrary threshold."
            },
            notApplicable = new
            {
                filesystem = "N/A: both measured paths operate on in-memory borrowed base pixels and owned result buffers.",
                sqlite = "N/A: host-neutral presentation rendering has no persistence boundary.",
                network = "N/A: no transport is exercised.",
                backlog = "N/A: the serial comparison has no queue or worker backlog."
            },
            limitations = new[]
            {
                "Endpoint RSS and CPU are process-wide and may include test-host/runtime activity.",
                "Retained LOH is a forced-GC path-group endpoint, not per-operation attribution.",
                "Baseline revision and identical legacy blobs establish ancestry/source equivalence only; both timed paths execute in the candidate binary.",
                "This synthetic Mono8 fixture complements but does not replace unavailable external canonical W6 evidence.",
                "Known full-frame counts are source-contract ownership counts; runtime allocation measurements include all managed allocations.",
                "Filesystem, SQLite, network, worker scheduling, and backlog behavior are outside this in-memory comparison."
            }
        };

        VerifyReceipt(root, receiptPath!);
        Assert.AreEqual(receiptSha256, Sha256(File.ReadAllBytes(receiptPath!)), "Build receipt changed during measurement.");
        Assert.AreEqual(runtimeBefore.Sha256, RuntimeInventory(Path.GetDirectoryName(typeof(Issue433LayeredPresentationPerformanceTests).Assembly.Location)!).Sha256);
        Directory.CreateDirectory(outputDirectory);
        using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, evidence, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        using (var written = JsonDocument.Parse(File.ReadAllBytes(outputPath)))
            Assert.AreEqual(ResultSchema, written.RootElement.GetProperty("schemaVersion").GetString());
        VerifyReceipt(root, receiptPath!);
        Assert.AreEqual(receiptSha256, Sha256(File.ReadAllBytes(receiptPath!)), "Build receipt changed while writing evidence.");
        Assert.AreEqual(runtimeBefore.Sha256, RuntimeInventory(Path.GetDirectoryName(typeof(Issue433LayeredPresentationPerformanceTests).Assembly.Location)!).Sha256);
        Console.WriteLine($"Issue #433 evidence: {outputPath}");
    }

    private static Fixture CreateFixture()
    {
        var basePixels = new byte[FrameBytes];
        var objects = new[]
        {
            new ProjectedAnnotationObject("zenith", "", new PixelPoint(1776, 1776), DrawLabel: false),
            new ProjectedAnnotationObject("guide", "", new PixelPoint(900, 1200), DrawLabel: false)
        };
        var segments = new[]
        {
            new ProjectedAnnotationSegment("ORI", new PixelPoint(900, 1200), new PixelPoint(1776, 1776))
        };
        var projection = new ProjectedAnnotationOverlay(new PixelPoint(1776, 1776), 1600,
            new PixelPoint(1776, 176), new PixelPoint(3376, 1776), new PixelPoint(1776, 3376), new PixelPoint(176, 1776));
        var metadata = new MetadataCornerOverlay(
            ["UTC 2026-08-25", "RIG SYNTHETIC"], ["EXPOSURE 30S"], ["ENV CLEAR 8C"], ["CAL V1"]);
        var cloudMask = new byte[32];
        for (var index = 0; index < 256; index += 17) cloudMask[index >> 3] |= (byte)(1 << (index & 7));
        Assert.AreEqual(ObjectCount, objects.Length);
        Assert.AreEqual(SegmentCount, segments.Length);
        return new(basePixels, new ImageLayout(Width, Height, CameraPixelFormat.Mono8, Width), objects, segments,
            projection, metadata, cloudMask, new AnnotationOptions
            {
                MarkRadius = 6,
                DrawLabels = false,
                MarkerValue = 144,
                DrawImageCircle = true,
                DrawCardinalDirections = false,
                ImageCircleValue = 96,
                ConstellationLineValue = 160,
                ConstellationLineOpacity = 0.8
            });
    }

    private static AnnotationResult RunOldAnnotation(Fixture fixture) => AnnotationRenderer.AnnotateMono8WithSegments(
        fixture.BasePixels, Width, Height, fixture.Objects, fixture.Segments, new PreviewTransform(1, 1),
        fixture.AnnotationOptions, fixture.Projection, fixture.Metadata);

    private static WeatherCloudOverlayResult RunOldWeather(Fixture fixture, ReadOnlyMemory<byte> annotation) =>
        WeatherCloudOverlayRenderer.Render(fixture.Layout, annotation, 16, 16, fixture.CloudMask, [],
            new WeatherCloudOverlayRenderOptions(DrawLabels: false));

    private static ReadOnlyMemory<byte> RunOld(Fixture fixture)
    {
        var annotation = RunOldAnnotation(fixture);
        return RunOldWeather(fixture, annotation.Pixels).Pixels;
    }

    private static byte[] RunLayered(Fixture fixture)
    {
        var structured = CreateLayeredFixture(fixture);
        return Materialize(fixture, structured);
    }

    private static OutputIdentity MeasureOldGroup(
        Fixture fixture,
        Process process,
        ICollection<BoundaryMeasurement> annotationMeasurements,
        ICollection<BoundaryMeasurement> weatherMeasurements,
        ICollection<BoundaryMeasurement> endToEndMeasurements,
        ICollection<GroupMemoryEndpoint> memoryEndpoints,
        ref MemoryEndpoint memoryCursor)
    {
        var before = memoryCursor;
        annotationMeasurements.Add(MeasureOutput(process, () => RunOldAnnotation(fixture).Pixels));
        var annotationForWeather = RunOldAnnotation(fixture).Pixels.ToArray();
        weatherMeasurements.Add(MeasureOutput(process, () => RunOldWeather(fixture, annotationForWeather).Pixels));
        annotationForWeather = null;
        var endToEnd = MeasureOutput(process, () => RunOld(fixture));
        endToEndMeasurements.Add(endToEnd);
        memoryCursor = CollectMemoryEndpoint(process);
        memoryEndpoints.Add(GroupMemoryEndpoint.Create(before, memoryCursor));
        return new(endToEnd.OutputChecksumSha256, endToEnd.OutputByteLength);
    }

    private static OutputIdentity MeasureLayeredGroup(
        Fixture fixture,
        Process process,
        ICollection<BoundaryMeasurement> constructionMeasurements,
        ICollection<BoundaryMeasurement> materializationMeasurements,
        ICollection<BoundaryMeasurement> endToEndMeasurements,
        ICollection<GroupMemoryEndpoint> memoryEndpoints,
        ref MemoryEndpoint memoryCursor,
        out long structuredBytes,
        out long manifestBytes)
    {
        var before = memoryCursor;
        LayeredFixture? structured = null;
        constructionMeasurements.Add(MeasureMetrics(process, () =>
        {
            structured = CreateLayeredFixture(fixture);
            return new OutputIdentity(StructuredChecksum(structured),
                checked((int)(structured.PayloadBytes.Sum(static value => (long)value.Length) + structured.ManifestBytes.Length)));
        }));
        Assert.IsNotNull(structured);
        structuredBytes = structured.PayloadBytes.Sum(static value => (long)value.Length);
        manifestBytes = structured.ManifestBytes.Length;
        materializationMeasurements.Add(MeasureOutput(process, () => Materialize(fixture, structured)));
        structured = null;
        var endToEnd = MeasureOutput(process, () => RunLayered(fixture));
        endToEndMeasurements.Add(endToEnd);
        memoryCursor = CollectMemoryEndpoint(process);
        memoryEndpoints.Add(GroupMemoryEndpoint.Create(before, memoryCursor));
        return new(endToEnd.OutputChecksumSha256, endToEnd.OutputByteLength);
    }

    private static LayeredFixture CreateLayeredFixture(Fixture fixture)
    {
        var mono = static (byte value) => new PresentationColor(value, value, value);
        var constellations = PresentationLayerPayloadJson.Create(new string('A', 64), Width, Height,
            segments: fixture.Segments.Select(item => new PresentationSegmentV1(item.FromPixel, item.ToPixel,
                fixture.AnnotationOptions.ConstellationLineThickness, mono(fixture.AnnotationOptions.ConstellationLineValue))).ToArray());
        var scene = PresentationLayerPayloadJson.Create(new string('A', 64), Width, Height,
            markers: fixture.Objects.Select(item => new PresentationMarkerV1(item.Pixel,
                fixture.AnnotationOptions.MarkRadius, mono(fixture.AnnotationOptions.MarkerValue))).ToArray());
        var projection = PresentationLayerPayloadJson.Create(new string('A', 64), Width, Height,
            ellipses: [new PresentationEllipseV1(fixture.Projection.Center, fixture.Projection.ImageCircleRadius,
                fixture.Projection.ImageCircleRadius, mono(fixture.AnnotationOptions.ImageCircleValue))]);
        var metadata = PresentationLayerPayloadJson.Create(new string('C', 64), Width, Height,
            textBlocks: CreateTextBlocks(fixture.Metadata));
        var cloud = PresentationLayerPayloadJson.Create(new string('B', 64), Width, Height,
            tileMask: new PresentationTileMaskV1(16, 16, PresentationTileMaskV1.RowMajorLsbFirst,
                fixture.CloudMask, 1, mono(byte.MaxValue)));
        var payloads = new[] { constellations, scene, projection, metadata, cloud };
        var payloadBytes = payloads.Select(PresentationLayerPayloadJson.Serialize).ToArray();
        var compatibility = new PresentationCompatibilityDescriptor(Width, Height, new string('D', 64), new string('E', 64));
        var baseReference = new PresentationProductReference(Guid.Parse("10000000-0000-0000-0000-000000000001"),
            new string('F', 64), "application/x-hvo-packed-image", compatibility);
        var layers = payloads.Select((payload, index) => LayeredPresentationJson.CreateLayer($"layer-{index}",
            new PresentationProductReference(Guid.Parse($"20000000-0000-0000-0000-{index + 1:D12}"), payload.ContentIdentitySha256,
                PresentationLayerPayloadJson.MediaType, compatibility), null, PresentationCoordinateSpace.ScenePixels,
            PresentationLayerCompositor.AlgorithmVersion, "issue-433-style-v1", index * 10,
            PresentationBlendMode.Normal, index == 0 ? 800_000 : 1_000_000, true,
            JsonSerializer.SerializeToElement(new { }))).ToArray();
        var manifest = LayeredPresentationJson.CreateManifest(baseReference, null, layers);
        return new(payloads, payloadBytes, LayeredPresentationJson.Serialize(manifest));
    }

    private static PresentationTextBlockV1[] CreateTextBlocks(MetadataCornerOverlay metadata)
    {
        var color = new PresentationColor(metadata.Value, metadata.Value, metadata.Value);
        return
        [
            new(PresentationTextAnchor.TopLeft, default, metadata.TopLeft, metadata.Scale, metadata.Inset, metadata.LineSpacing, color),
            new(PresentationTextAnchor.TopRight, default, metadata.TopRight, metadata.Scale, metadata.Inset, metadata.LineSpacing, color),
            new(PresentationTextAnchor.BottomLeft, default, metadata.BottomLeft, metadata.Scale, metadata.Inset, metadata.LineSpacing, color),
            new(PresentationTextAnchor.BottomRight, default, metadata.BottomRight, metadata.Scale, metadata.Inset, metadata.LineSpacing, color)
        ];
    }

    private static byte[] Materialize(Fixture fixture, LayeredFixture structured) => PresentationLayerCompositor.Composite(
        fixture.Layout, fixture.BasePixels,
        structured.Payloads.Select((payload, index) => new PresentationCompositorLayer(payload, true,
            PresentationRasterBlendMode.Normal, index == 0 ? 800_000 : 1_000_000)).ToArray());

    private static BoundaryMeasurement MeasureOutput(Process process, Func<ReadOnlyMemory<byte>> operation) =>
        MeasureMetrics(process, () =>
        {
            return ConsumeOutput(operation);
        });

    private static OutputIdentity ConsumeOutput(Func<ReadOnlyMemory<byte>> operation)
    {
        var output = operation();
        var identity = new OutputIdentity(Sha256(output.Span), output.Length);
        output = default;
        return identity;
    }

    private static BoundaryMeasurement MeasureMetrics(Process process, Func<OutputIdentity> operation)
    {
        process.Refresh();
        var rssBefore = process.WorkingSet64;
        var cpuBefore = process.TotalProcessorTime;
        var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var result = operation();
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        process.Refresh();
        return new(elapsed, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds, allocated,
            rssBefore, process.WorkingSet64, result.ChecksumSha256, result.ByteLength);
    }

    private static MemoryEndpoint CollectMemoryEndpoint(Process process)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        process.Refresh();
        return new(process.WorkingSet64, RetainedMemory());
    }

    private static object MemorySummary(IReadOnlyCollection<GroupMemoryEndpoint> endpoints) => new
    {
        sampleCount = endpoints.Count,
        rssBeforeBytes = NumericSummary(endpoints.Select(static value => (double)value.RssBeforeBytes)),
        rssAfterBytes = NumericSummary(endpoints.Select(static value => (double)value.RssAfterBytes)),
        rssChangeBytes = NumericSummary(endpoints.Select(static value => (double)(value.RssAfterBytes - value.RssBeforeBytes))),
        retainedManagedBeforeBytes = NumericSummary(endpoints.Select(static value => (double)value.ManagedBeforeBytes)),
        retainedManagedAfterBytes = NumericSummary(endpoints.Select(static value => (double)value.ManagedAfterBytes)),
        retainedManagedChangeBytes = NumericSummary(endpoints.Select(static value => (double)(value.ManagedAfterBytes - value.ManagedBeforeBytes))),
        retainedLohBeforeBytes = NumericSummary(endpoints.Select(static value => (double)value.LohBeforeBytes)),
        retainedLohAfterBytes = NumericSummary(endpoints.Select(static value => (double)value.LohAfterBytes)),
        retainedLohChangeBytes = NumericSummary(endpoints.Select(static value => (double)(value.LohAfterBytes - value.LohBeforeBytes)))
    };

    private static object NumericSummary(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return new { median = Percentile(sorted, 0.5), minimum = sorted[0], maximum = sorted[^1] };
    }

    private static string StructuredChecksum(LayeredFixture fixture)
    {
        using var stream = new MemoryStream();
        foreach (var payload in fixture.PayloadBytes) stream.Write(payload);
        stream.Write(fixture.ManifestBytes);
        return Sha256(stream.ToArray());
    }

    private static MeasurementSummary Summary(IReadOnlyCollection<BoundaryMeasurement> measurements)
    {
        var wall = measurements.Select(static item => item.WallMilliseconds).Order().ToArray();
        var totalSeconds = wall.Sum() / 1000d;
        return new(Percentile(wall, 0.5), Percentile(wall, 0.95),
            measurements.Average(static item => (double)item.CpuMilliseconds),
            measurements.Average(static item => (double)item.AllocatedBytes),
            measurements.Min(static item => item.RssBeforeBytes), measurements.Max(static item => item.RssAfterBytes),
            measurements.Count / totalSeconds);
    }

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];

    internal static PercentComparison PercentChange(double oldValue, double newValue)
    {
        if (!double.IsFinite(oldValue) || !double.IsFinite(newValue))
            return new(null, "N/A: comparison inputs must both be finite.");
        if (oldValue == 0)
            return new(null, "N/A: legacy denominator is zero.");
        var percent = (newValue - oldValue) / oldValue * 100;
        return double.IsFinite(percent)
            ? new(percent, null)
            : new(null, "N/A: percent change is non-finite.");
    }

    internal static string[] MaterialFlags(IReadOnlyDictionary<string, PercentComparison> changes) => changes
        .Where(static item => item.Value.Percent is { } value && double.IsFinite(value) && Math.Abs(value) > 10)
        .Select(static item => item.Key).ToArray();

    private static RetainedMemoryMeasurement RetainedMemory()
    {
        var info = GC.GetGCMemoryInfo();
        var loh = info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : -1;
        return new(GC.GetTotalMemory(forceFullCollection: false), loh);
    }

    private static BuildReceipt VerifyReceipt(string root, string path)
    {
        Assert.IsTrue(File.Exists(path), "Issue #433 build receipt is missing.");
        var receipt = JsonSerializer.Deserialize<BuildReceipt>(File.ReadAllBytes(path), JsonOptions);
        Assert.IsNotNull(receipt);
        Assert.AreEqual("issue-433-build-receipt-v1", receipt.SchemaVersion);
        Assert.AreEqual("10.0.100", receipt.SdkVersion);
        Assert.AreEqual(ReadProcess(root, "dotnet", "--version"), receipt.SdkVersion);
        var evidenceHead = ReadGit(root, "rev-parse", "HEAD");
        var evidenceHeadTree = ReadGit(root, "rev-parse", "HEAD^{tree}");
        var expectedBaselineRevision = Environment.GetEnvironmentVariable("HVO_ISSUE433_BASELINE") ?? "fbf93c1";
        var expectedCandidateRevision = Environment.GetEnvironmentVariable("HVO_ISSUE433_CANDIDATE") ?? "ee08ee7";
        var baselineCommit = ReadGit(root, "rev-parse", $"{expectedBaselineRevision}^{{commit}}");
        var baselineTree = ReadGit(root, "rev-parse", $"{baselineCommit}^{{tree}}");
        var candidateProductCommit = ReadGit(root, "rev-parse", $"{expectedCandidateRevision}^{{commit}}");
        var candidateProductTree = ReadGit(root, "rev-parse", $"{candidateProductCommit}^{{tree}}");
        var candidateParent = ReadGit(root, "rev-parse", $"{candidateProductCommit}^");
        Assert.AreEqual(evidenceHead, receipt.EvidenceHead);
        Assert.AreEqual(evidenceHeadTree, receipt.EvidenceHeadTree);
        Assert.AreEqual(candidateProductCommit, receipt.CandidateProductCommit);
        Assert.AreEqual(candidateProductTree, receipt.CandidateProductTree);
        Assert.AreEqual(baselineCommit, candidateParent, "Configured issue #433 candidate product parent must exactly equal baseline.");
        AssertGitSuccess(root, "merge-base", "--is-ancestor", candidateProductCommit, evidenceHead);
        Assert.AreEqual(baselineCommit, receipt.BaselineCommit);
        Assert.AreEqual(baselineTree, receipt.BaselineTree);
        Assert.AreEqual(string.Empty, ReadGit(root, "status", "--porcelain", "--untracked-files=all"));
        Assert.AreEqual(Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"), receipt.EvidenceHead);
        Assert.AreEqual(Environment.GetEnvironmentVariable("HVO_EVIDENCE_TRIAL"), receipt.Trial);
        Assert.AreEqual("Release", receipt.Configuration);
        Assert.AreEqual("tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj", receipt.Project);
        var productDiffSha256 = Sha256(ReadGitBytes(root, "diff", "--binary", $"{baselineCommit}...{candidateProductCommit}"));
        var harnessDiffSha256 = Sha256(ReadGitBytes(root, "diff", "--binary", $"{candidateProductCommit}...{evidenceHead}"));
        Assert.AreEqual(productDiffSha256, receipt.BaselineToCandidateProductDiffSha256, ignoreCase: true);
        Assert.AreEqual(harnessDiffSha256, receipt.CandidateToEvidenceHarnessDiffSha256, ignoreCase: true);
        var evidencePaths = Encoding.UTF8.GetString(ReadGitBytes(root, "diff", "--name-only", "-z",
            $"{candidateProductCommit}...{evidenceHead}")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        CollectionAssert.AreEquivalent(EvidenceOnlyPaths, evidencePaths);
        var sourceInventory = SourceInventory(root);
        Assert.AreEqual(sourceInventory.Sha256, receipt.MeasuredSourceInventorySha256, ignoreCase: true);
        Assert.AreEqual(sourceInventory.FileCount, receipt.MeasuredSourceInventoryFileCount);
        VerifyLegacyBlobProofs(root, receipt.LegacyBlobProofs, baselineCommit, candidateProductCommit, evidenceHead);
        return receipt;
    }

    private static InventoryHash SourceInventory(string root)
    {
        var bytes = ReadGitBytes(root, ["ls-files", "-z", "--", .. SourcePathSpecs]);
        var files = Encoding.UTF8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.IsNotEmpty(files, "Issue #433 source inventory is empty.");
        return HashInventory(root, files);
    }

    private static InventoryHash HashInventory(string root, string[] relativePaths)
    {
        using var manifest = new MemoryStream();
        foreach (var relative in relativePaths)
        {
            Assert.IsFalse(relative.Contains('\n', StringComparison.Ordinal) || relative.Contains('\t', StringComparison.Ordinal),
                "Unsupported inventory path.");
            var content = File.ReadAllBytes(Path.Combine(root, relative));
            manifest.Write(Encoding.UTF8.GetBytes($"{relative}\t{content.LongLength}\t{Sha256(content)}\n"));
        }
        return new(relativePaths.Length, Sha256(manifest.ToArray()));
    }

    private static void VerifyLegacyBlobProofs(
        string root, JsonElement proofs, string baselineCommit, string candidateProductCommit, string evidenceHead)
    {
        Assert.AreEqual(JsonValueKind.Array, proofs.ValueKind);
        var byPath = proofs.EnumerateArray().ToDictionary(
            static proof => proof.GetProperty("path").GetString()!, StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(LegacySourcePaths, byPath.Keys.ToArray());
        foreach (var relativePath in LegacySourcePaths)
        {
            var baselineBlob = ReadGit(root, "rev-parse", $"{baselineCommit}:{relativePath}");
            var candidateBlob = ReadGit(root, "rev-parse", $"{candidateProductCommit}:{relativePath}");
            var evidenceBlob = ReadGit(root, "rev-parse", $"{evidenceHead}:{relativePath}");
            Assert.AreEqual(baselineBlob, candidateBlob, $"Legacy source changed: {relativePath}");
            Assert.AreEqual(candidateBlob, evidenceBlob, $"Evidence-only commits changed production source: {relativePath}");
            var proof = byPath[relativePath];
            Assert.AreEqual(baselineBlob, proof.GetProperty("baselineBlob").GetString());
            Assert.AreEqual(candidateBlob, proof.GetProperty("candidateProductBlob").GetString());
            Assert.AreEqual(evidenceBlob, proof.GetProperty("evidenceHeadBlob").GetString());
            Assert.IsTrue(proof.GetProperty("identical").GetBoolean());
        }
    }

    private static RuntimeOutput RuntimeInventory(string outputDirectory)
    {
        var files = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(outputDirectory, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal).ToArray();
        var inventory = HashInventory(outputDirectory, files);
        return new(inventory.FileCount, inventory.Sha256);
    }

    private static string ReadGit(string root, params string[] arguments)
        => ReadProcess(root, "git", arguments);

    private static byte[] ReadGitBytes(string root, params string[] arguments)
        => ReadProcessBytes(root, "git", arguments);

    private static string ReadProcess(string root, string fileName, params string[] arguments)
        => Encoding.UTF8.GetString(ReadProcessBytes(root, fileName, arguments)).Trim();

    private static byte[] ReadProcessBytes(string root, string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, error);
        return output.ToArray();
    }

    private static void AssertGitSuccess(string root, params string[] arguments) =>
        _ = ReadGitBytes(root, arguments);

    private static string Sha256(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string BuildConfiguration => typeof(Issue433LayeredPresentationPerformanceTests).Assembly
        .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;

    private sealed record Fixture(byte[] BasePixels, ImageLayout Layout, ProjectedAnnotationObject[] Objects,
        ProjectedAnnotationSegment[] Segments, ProjectedAnnotationOverlay Projection, MetadataCornerOverlay Metadata,
        byte[] CloudMask, AnnotationOptions AnnotationOptions)
    {
        internal int ObjectCount => Objects.Length;
        internal int SegmentCount => Segments.Length;
    }
    private sealed record LayeredFixture(PresentationLayerPayloadV1[] Payloads, byte[][] PayloadBytes, byte[] ManifestBytes);
    private sealed record BoundaryMeasurement(double WallMilliseconds, double CpuMilliseconds,
        long AllocatedBytes, long RssBeforeBytes, long RssAfterBytes, string OutputChecksumSha256, int OutputByteLength);
    private sealed record MeasurementSummary(double MedianWallMilliseconds, double P95WallMilliseconds,
        double CpuMillisecondsPerOperation, double AllocatedBytesPerOperation, long MinimumRssBeforeBytes,
        long MaximumRssAfterBytes, double OperationsPerSecond);
    private readonly record struct OutputIdentity(string ChecksumSha256, int ByteLength);
    private sealed record MemoryEndpoint(long RssBytes, RetainedMemoryMeasurement Retained);
    private sealed record GroupMemoryEndpoint(long RssBeforeBytes, long RssAfterBytes,
        long ManagedBeforeBytes, long ManagedAfterBytes, long LohBeforeBytes, long LohAfterBytes)
    {
        internal static GroupMemoryEndpoint Create(MemoryEndpoint before, MemoryEndpoint after) => new(
            before.RssBytes, after.RssBytes, before.Retained.ManagedBytes, after.Retained.ManagedBytes,
            before.Retained.LohBytes, after.Retained.LohBytes);
    }
    private sealed record RetainedMemoryMeasurement(long ManagedBytes, long LohBytes);
    private sealed record RuntimeOutput(int FileCount, string Sha256);
    private readonly record struct InventoryHash(int FileCount, string Sha256);
    internal sealed record PercentComparison(double? Percent, string? NotApplicableReason);
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "System.Text.Json constructs the receipt DTO.")]
    private sealed record BuildReceipt(string SchemaVersion, string EvidenceHead, string EvidenceHeadTree,
        string CandidateProductCommit, string CandidateProductTree, string BaselineCommit, string BaselineTree,
        string BaselineToCandidateProductDiffSha256, string CandidateToEvidenceHarnessDiffSha256,
        string MeasuredSourceInventorySha256, int MeasuredSourceInventoryFileCount,
        string RuntimeOutputSetSha256, int RuntimeOutputFileCount,
        string Configuration, string Project, string SdkVersion, string Trial, JsonElement LegacyBlobProofs);
}

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires a public test class.")]
public sealed class Issue433LayeredPresentationPerformanceHarnessTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void ZeroDenominatorComparisonSerializesAsExplicitNotApplicable()
    {
        var comparison = Issue433LayeredPresentationPerformanceTests.PercentChange(0, 1);
        var json = JsonSerializer.Serialize(comparison, WebJson);
        using var document = JsonDocument.Parse(json);

        Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("percent").ValueKind);
        StringAssert.Contains(document.RootElement.GetProperty("notApplicableReason").GetString()!, "denominator is zero", StringComparison.Ordinal);
        Assert.IsFalse(json.Contains("NaN", StringComparison.Ordinal));
        Assert.IsEmpty(Issue433LayeredPresentationPerformanceTests.MaterialFlags(
            new Dictionary<string, Issue433LayeredPresentationPerformanceTests.PercentComparison>(StringComparer.Ordinal)
            {
                ["zero"] = comparison,
                ["nonFinite"] = Issue433LayeredPresentationPerformanceTests.PercentChange(double.PositiveInfinity, 1)
            }));
    }

    [TestMethod]
    public void ManualHarnessAndRuntimeManifestRemainPinned()
    {
        var root = RepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "tests", "HVO.SkyMonitor.Processing.Tests",
            "Issue433LayeredPresentationPerformanceTests.cs"));
        StringAssert.Contains(source, "internal const int WarmupCount = 5;", StringComparison.Ordinal);
        StringAssert.Contains(source, "internal const int MeasuredCount = 30;", StringComparison.Ordinal);
        var type = typeof(Issue433LayeredPresentationPerformanceTests);
        AssertConstant(type: typeof(Issue433LayeredPresentationPerformanceTests), "Width", 3552);
        AssertConstant(type: typeof(Issue433LayeredPresentationPerformanceTests), "Height", 3552);
        AssertConstant(type: typeof(Issue433LayeredPresentationPerformanceTests), "FrameBytes", 12_616_704);
        AssertConstant(type: typeof(Issue433LayeredPresentationPerformanceTests), "ObjectCount", 2);
        AssertConstant(type: typeof(Issue433LayeredPresentationPerformanceTests), "SegmentCount", 1);
        var method = type.GetMethod(nameof(Issue433LayeredPresentationPerformanceTests.FullResolutionSyntheticMono8OldAndLayeredPathsEvidence));
        Assert.IsNotNull(method);
        Assert.HasCount(1, method.GetCustomAttributes<TestMethodAttribute>());
        CollectionAssert.Contains(type.GetCustomAttributes<TestCategoryAttribute>().SelectMany(static value => value.TestCategories).ToArray(), "Manual");
        CollectionAssert.DoesNotContain(type.GetCustomAttributes<TestCategoryAttribute>().SelectMany(static value => value.TestCategories).ToArray(), "Unit");
        Assert.HasCount(1, type.GetCustomAttributes<DoNotParallelizeAttribute>());

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "docs", "validation", "issue-433-runtime-signals.json")));
        Assert.AreEqual("issue-433-runtime-signals-v1", manifest.RootElement.GetProperty("schemaVersion").GetString());
        Assert.AreEqual("issue-433-layered-presentation-performance-v1", manifest.RootElement.GetProperty("resultSchema").GetString());
        Assert.AreEqual("scripts/evidence:issue-433", manifest.RootElement.GetProperty("runner").GetString());
        Assert.IsTrue(manifest.RootElement.GetProperty("retainedTrials").GetBoolean());
        Assert.IsTrue(manifest.RootElement.GetProperty("performanceWorkloads").EnumerateArray()
            .All(static value => value.GetString()!.StartsWith("synthetic-3552x3552-mono8-", StringComparison.Ordinal)));
        var mappings = manifest.RootElement.GetProperty("resultFieldMappings").EnumerateObject()
            .ToDictionary(static property => property.Name, static property => property.Value.GetString()!, StringComparer.Ordinal);
        foreach (var measurement in manifest.RootElement.GetProperty("measurements").EnumerateArray().Select(static value => value.GetString()!))
        {
            Assert.IsTrue(mappings.TryGetValue(measurement, out var field), measurement);
            StringAssert.Contains(source, field.Split('.')[0], StringComparison.OrdinalIgnoreCase);
        }

        var runner = File.ReadAllText(Path.Combine(root, "scripts", "evidence:issue-433"));
        StringAssert.Contains(runner, "EXPECTED_BASELINE=${HVO_ISSUE433_BASELINE:-fbf93c1}", StringComparison.Ordinal);
        StringAssert.Contains(runner, "EXPECTED_CANDIDATE=${HVO_ISSUE433_CANDIDATE:-ee08ee7}", StringComparison.Ordinal);
        StringAssert.Contains(runner, "--no-incremental -warnaserror", StringComparison.Ordinal);
        StringAssert.Contains(runner, "dotnet clean \"$SOLUTION\" --configuration Release", StringComparison.Ordinal);
        StringAssert.Contains(runner, "git merge-base --is-ancestor \"$CANDIDATE_PRODUCT_COMMIT\" \"$EVIDENCE_HEAD\"", StringComparison.Ordinal);
        StringAssert.Contains(runner, "git diff --binary \"$BASELINE_COMMIT...$CANDIDATE_PRODUCT_COMMIT\"", StringComparison.Ordinal);
        StringAssert.Contains(runner, "git diff --binary \"$CANDIDATE_PRODUCT_COMMIT...$EVIDENCE_HEAD\"", StringComparison.Ordinal);
        StringAssert.Contains(runner, "EXPECTED_EVIDENCE_PATHS=(", StringComparison.Ordinal);
        StringAssert.Contains(runner, "export HVO_ISSUE433_BASELINE=\"$BASELINE_COMMIT\"", StringComparison.Ordinal);
        StringAssert.Contains(runner, "export HVO_ISSUE433_CANDIDATE=\"$CANDIDATE_PRODUCT_COMMIT\"", StringComparison.Ordinal);
        StringAssert.Contains(runner, "set -o noclobber", StringComparison.Ordinal);
        StringAssert.Contains(runner, "PresentationLayerPayload.cs", StringComparison.Ordinal);
        StringAssert.Contains(runner, "git cat-file -e", StringComparison.Ordinal);
        StringAssert.Contains(runner, nameof(Issue433LayeredPresentationPerformanceTests.FullResolutionSyntheticMono8OldAndLayeredPathsEvidence), StringComparison.Ordinal);
        StringAssert.Contains(source, "new FileStream(outputPath, FileMode.CreateNew", StringComparison.Ordinal);
        StringAssert.Contains(source, "VerifyReceipt(root, receiptPath!)", StringComparison.Ordinal);
        StringAssert.Contains(source, "RuntimeInventory(Path.GetDirectoryName", StringComparison.Ordinal);
        StringAssert.Contains(source, "legacyPathInCandidateBinary", StringComparison.Ordinal);
        StringAssert.Contains(source, "ancestry-and-source-equivalence-only", StringComparison.Ordinal);
        StringAssert.Contains(source, "no separate baseline build was measured", StringComparison.Ordinal);
        StringAssert.Contains(source, "canonicalW6 = false", StringComparison.Ordinal);
        StringAssert.Contains(source, "id = \"synthetic-3552x3552-mono8-old-and-layered\"", StringComparison.Ordinal);
        StringAssert.Contains(source, "annotationIntermediateBytesPerOperation = (long)FrameBytes", StringComparison.Ordinal);
        StringAssert.Contains(source, "weatherFinalBytesPerOperation = (long)FrameBytes", StringComparison.Ordinal);
        StringAssert.Contains(source, "totalPublishedArtifactBytesPerOperation = 2L * FrameBytes", StringComparison.Ordinal);
        StringAssert.Contains(source, "transientKnownFullFrameBytesPerOperation = 4L * FrameBytes", StringComparison.Ordinal);
        StringAssert.Contains(source, "knownFullFrameAllocationsPerOperation = 1", StringComparison.Ordinal);
        StringAssert.Contains(source, "new(null, \"N/A: legacy denominator is zero.\")", StringComparison.Ordinal);
        StringAssert.Contains(source, "double.IsFinite(percent)", StringComparison.Ordinal);
        StringAssert.Contains(source, "item.Value.Percent is { } value && double.IsFinite(value) && Math.Abs(value) > 10", StringComparison.Ordinal);
        StringAssert.Contains(source, "var baselineCommit = ReadGit(root, \"rev-parse\"", StringComparison.Ordinal);
        StringAssert.Contains(source, "Assert.AreEqual(baselineCommit, candidateParent", StringComparison.Ordinal);
        StringAssert.Contains(source, "AssertGitSuccess(root, \"merge-base\", \"--is-ancestor\"", StringComparison.Ordinal);
        StringAssert.Contains(source, "var baselineTree = ReadGit(root, \"rev-parse\"", StringComparison.Ordinal);
        StringAssert.Contains(source, "ReadGitBytes(root, \"diff\", \"--binary\"", StringComparison.Ordinal);
        StringAssert.Contains(source, "CollectionAssert.AreEquivalent(EvidenceOnlyPaths, evidencePaths)", StringComparison.Ordinal);
        StringAssert.Contains(source, "var sourceInventory = SourceInventory(root);", StringComparison.Ordinal);
        StringAssert.Contains(source, "VerifyLegacyBlobProofs(root, receipt.LegacyBlobProofs", StringComparison.Ordinal);
        StringAssert.Contains(source, "CollectionAssert.AreEquivalent(LegacySourcePaths", StringComparison.Ordinal);
        StringAssert.Contains(source, "Assert.AreEqual(\"10.0.100\", receipt.SdkVersion)", StringComparison.Ordinal);
        StringAssert.Contains(source, "ReadProcess(root, \"dotnet\", \"--version\")", StringComparison.Ordinal);
        StringAssert.Contains(source, "Production source is pinned to candidateProductCommit", StringComparison.Ordinal);
        StringAssert.Contains(source, "receipt.EvidenceHeadTree", StringComparison.Ordinal);
        StringAssert.Contains(source, "receipt.CandidateProductTree", StringComparison.Ordinal);
        StringAssert.Contains(source, "receipt.BaselineToCandidateProductDiffSha256", StringComparison.Ordinal);
        StringAssert.Contains(source, "receipt.CandidateToEvidenceHarnessDiffSha256", StringComparison.Ordinal);
        StringAssert.Contains(source, "receipt.MeasuredSourceInventorySha256", StringComparison.Ordinal);
        var boundaryType = type.GetNestedType("BoundaryMeasurement", BindingFlags.NonPublic);
        Assert.IsNotNull(boundaryType);
        Assert.IsNull(boundaryType.GetProperty("Result"));
        Assert.IsFalse(boundaryType.GetProperties().Any(static property => property.PropertyType == typeof(byte[])));
        Assert.IsNotNull(boundaryType.GetProperty("OutputChecksumSha256"));
        Assert.IsNotNull(boundaryType.GetProperty("OutputByteLength"));
        StringAssert.Contains(source, "output = default;", StringComparison.Ordinal);
        AssertBashSyntax(root, Path.Combine(root, "scripts", "evidence:issue-433"));
    }

    private static void AssertConstant(Type type, string name, int expected)
    {
        var field = type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        Assert.AreEqual(expected, field.GetRawConstantValue());
    }

    private static void AssertBashSyntax(string root, string script)
    {
        var start = new ProcessStartInfo("bash")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start bash.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, string.Concat(output, error));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
