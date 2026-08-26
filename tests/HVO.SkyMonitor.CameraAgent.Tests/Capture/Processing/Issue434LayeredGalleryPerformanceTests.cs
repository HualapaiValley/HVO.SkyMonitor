using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    private const int Issue434UncachedMeasurements = 15;
    private const int Issue434CachedMeasurements = 100;
    private const int Issue434ConcurrentReaders = 8;
    private static readonly JsonSerializerOptions Issue434JsonOptions = new() { WriteIndented = true };

    [TestMethod]
    [TestCategory("Performance")]
    public async Task Issue434OfflineGalleryGenerationCacheConcurrencyAndMaterializationEvidence()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = CreatePersistence(fixture.Options, store, storage, telemetry);
            var graph = await CreateProductionLayeredGraphAsync(fixture, root).ConfigureAwait(false);
            var processed = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item, graph, persistence, telemetry, 1, NullLogger.Instance, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Outcome, processed.Reason);
            var captureId = fixture.Manifest.Descriptor.Capture.CaptureId;
            var manifestNode = await store.ReadNodeAsync(captureId, "overlay-manifest", CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(manifestNode);
            var manifestOutput = manifestNode.Outputs.Single();
            var manifest = LayeredPresentationJson.ParseManifest(
                await File.ReadAllBytesAsync(Path.Combine(root, manifestOutput.PayloadRelativePath)).ConfigureAwait(false))
                .Document;
            Assert.IsNotNull(manifest);
            var rawJournal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 5);
            await rawJournal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using var artifacts = new CameraAgentArtifactService(fixture.Options, store, new CameraAgentPreviewEncoder());

            var uncached = new double[Issue434UncachedMeasurements];
            CameraAgentLayeredPresentation? reference = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var uncachedAllocatedStart = GC.GetTotalAllocatedBytes(precise: true);
            for (var index = 0; index < uncached.Length; index++)
            {
                var service = new CameraAgentLayeredPresentationService(store, artifacts);
                var started = Stopwatch.GetTimestamp();
                var result = await service.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
                uncached[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, result.Status, result.Reason);
                Assert.IsNotNull(result.Presentation);
                reference ??= result.Presentation;
                CollectionAssert.AreEqual(reference.Svg.ToArray(), result.Presentation.Svg.ToArray());
            }
            var uncachedAllocated = GC.GetTotalAllocatedBytes(precise: true) - uncachedAllocatedStart;

            var cachedService = new CameraAgentLayeredPresentationService(store, artifacts);
            var warm = await cachedService.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(warm.Presentation);
            var cached = new double[Issue434CachedMeasurements];
            var cachedAllocatedStart = GC.GetTotalAllocatedBytes(precise: true);
            for (var index = 0; index < cached.Length; index++)
            {
                var started = Stopwatch.GetTimestamp();
                var result = await cachedService.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
                cached[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Assert.AreSame(warm.Presentation, result.Presentation);
            }
            var cachedAllocated = GC.GetTotalAllocatedBytes(precise: true) - cachedAllocatedStart;

            var concurrentService = new CameraAgentLayeredPresentationService(store, artifacts);
            var concurrencyStarted = Stopwatch.GetTimestamp();
            var concurrent = await Task.WhenAll(Enumerable.Range(0, Issue434ConcurrentReaders)
                .Select(_ => concurrentService.GetAsync(captureId, CancellationToken.None).AsTask())).ConfigureAwait(false);
            var concurrencyMilliseconds = Stopwatch.GetElapsedTime(concurrencyStarted).TotalMilliseconds;
            Assert.IsTrue(concurrent.All(static result => result.Status == CameraAgentLayeredPresentationStatus.Found));
            Assert.IsTrue(concurrent.All(result => ReferenceEquals(concurrent[0].Presentation, result.Presentation)));

            var selected = manifest.Layers.Take(2).Select(static layer => layer.LayerIdentitySha256).ToArray();
            var materializationStarted = Stopwatch.GetTimestamp();
            var materialized = await persistence.MaterializePresentationAsync(
                captureId, manifestOutput.ArtifactId, selected, "issue-434-evidence", CancellationToken.None)
                .ConfigureAwait(false);
            var materializationMilliseconds = Stopwatch.GetElapsedTime(materializationStarted).TotalMilliseconds;
            var replayStarted = Stopwatch.GetTimestamp();
            var replay = await persistence.MaterializePresentationAsync(
                captureId, manifestOutput.ArtifactId, selected, "issue-434-evidence", CancellationToken.None)
                .ConfigureAwait(false);
            var replayMilliseconds = Stopwatch.GetElapsedTime(replayStarted).TotalMilliseconds;
            Assert.IsFalse(materialized.Replayed);
            Assert.IsTrue(replay.Replayed);
            Assert.AreEqual(materialized.ArtifactId, replay.ArtifactId);
            Assert.AreEqual(materialized.ChecksumSha256, replay.ChecksumSha256);

            var svg = warm.Presentation.Svg.ToArray();
            var svgDocument = XDocument.Parse(System.Text.Encoding.UTF8.GetString(svg));
            var svgElementCount = svgDocument.Descendants().Count();
            Assert.IsLessThanOrEqualTo(CameraAgentLayeredPresentationService.MaximumSvgBytes, svg.Length);
            Assert.IsLessThanOrEqualTo(CameraAgentLayeredPresentationService.MaximumSvgElements, svgElementCount);
            Assert.IsLessThan(500d, Percentile(uncached, .95), "Uncached grouped SVG p95 exceeded the supplemental fixture bound.");
            Assert.IsLessThan(25d, Percentile(cached, .95), "Cached grouped SVG p95 exceeded the supplemental fixture bound.");
            Assert.IsLessThan(2000d, materializationMilliseconds, "Materialization exceeded the supplemental fixture bound.");

            if (string.Equals(Environment.GetEnvironmentVariable("HVO_ISSUE434_EVIDENCE"), "1", StringComparison.Ordinal))
            {
                WriteIssue434Evidence(new
                {
                    schemaVersion = "issue-434-layered-gallery-performance-v1",
                    revision = RequiredEnvironment("HVO_EVIDENCE_REVISION"),
                    trial = RequiredEnvironment("HVO_EVIDENCE_TRIAL"),
                    measuredUtc = DateTimeOffset.UtcNow,
                    fixture = new
                    {
                        kind = "production-graph-supplemental",
                        widthPixels = warm.Presentation.WidthPixels,
                        heightPixels = warm.Presentation.HeightPixels,
                        layerCount = warm.Presentation.Layers.Count,
                        expectedMetadataReadsPerUncachedRequest = warm.Presentation.Layers.Count + 2
                    },
                    svg = new
                    {
                        bytes = svg.Length,
                        elementCount = svgElementCount,
                        checksumSha256 = warm.Presentation.SvgChecksumSha256,
                        deterministicAcrossUncachedRequests = true
                    },
                    uncached = Metrics(uncached, uncachedAllocated),
                    cached = Metrics(cached, cachedAllocated),
                    concurrency = new
                    {
                        readers = Issue434ConcurrentReaders,
                        milliseconds = concurrencyMilliseconds,
                        sharedSingleFlightResult = true
                    },
                    materialization = new
                    {
                        milliseconds = materializationMilliseconds,
                        replayMilliseconds,
                        materialized.ByteLength,
                        materialized.ChecksumSha256,
                        materialized.OutputIdentitySha256,
                        replayConverged = replay.ArtifactId == materialized.ArtifactId
                    },
                    process = new
                    {
                        processorCount = Environment.ProcessorCount,
                        peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                        framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                        architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
                    }
                });
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static object Metrics(double[] values, long allocatedBytes) => new
    {
        measurements = values.Length,
        medianMilliseconds = Percentile(values, .5),
        p95Milliseconds = Percentile(values, .95),
        maximumMilliseconds = values.Max(),
        allocatedBytesPerOperation = allocatedBytes / (double)values.Length
    };

    private static double Percentile(double[] values, double percentile)
    {
        var ordered = values.Order().ToArray();
        return ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];
    }

    private static void WriteIssue434Evidence(object value)
    {
        var directory = Path.Combine(
            RequiredEnvironment("HVO_ISSUE434_EVIDENCE_ROOT"),
            RequiredEnvironment("HVO_EVIDENCE_REVISION"),
            "trials",
            RequiredEnvironment("HVO_EVIDENCE_TRIAL"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "issue-434-layered-gallery-performance.json");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, Issue434JsonOptions);
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required for issue #434 evidence.");
}
