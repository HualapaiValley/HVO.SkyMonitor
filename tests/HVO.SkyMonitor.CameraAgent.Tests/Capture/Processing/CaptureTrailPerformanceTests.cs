using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Manual")]
    public async Task CaptureTrail_W1CompositionCacheAndFrozenSaveEvidence()
    {
        const int width = 1936;
        const int height = 1216;
        var root = CreateTestRoot();
        try
        {
            var template = await CreateFixtureAsync(root).ConfigureAwait(false);
            var pixels = new byte[width * height * 2];
            for (var index = 0; index < pixels.Length; index += 2)
            {
                pixels[index] = (byte)(index / 2 % 251);
                pixels[index + 1] = 1;
            }
            var manifest = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono16,
                width, height, width * 2, pixels);
            manifest = manifest with
            {
                RelativeArtifactPath = "raw.bin",
                Descriptor = manifest.Descriptor with { Profiles = template.Manifest.Descriptor.Profiles }
            };
            var descriptor = manifest.Descriptor;
            var captureId = descriptor.Capture.CaptureId;
            var bytes = CaptureContractJson.Serialize(manifest);
            await File.WriteAllBytesAsync(Path.Combine(root, "raw.bin"), pixels).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(root, "raw.json"), bytes).ConfigureAwait(false);
            Assert.IsTrue(FrameReconstructor.TryReconstruct(descriptor, pixels, out var frame).IsValid);
            var receipt = new RawCaptureReceipt(RawIngressOutcome.Committed, manifest,
                new StoredFrameReference("raw.bin", Path.Combine(root, "raw.bin"), descriptor.Timing.ExposureStartedUtc,
                    FrameArtifactRole.Raw), CaptureContractJson.ComputeManifestSha256(manifest));
            var item = template.Item with { Submission = CreateSubmission(frame!), RawCapture = receipt };
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 5);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await journal.ReserveIdentityAsync(descriptor.Capture.AgentId, captureId, descriptor.Artifact.ArtifactId,
                CancellationToken.None).ConfigureAwait(false);
            await journal.CommitAsync(new RawIngressJournalEntry(descriptor.Capture.AgentId, descriptor.Capture.CaptureSequence,
                captureId, descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor),
                CaptureContractJson.ComputeManifestSha256(bytes), descriptor.Artifact.ChecksumSha256, pixels.Length,
                "raw.bin", "raw.json", bytes, descriptor.Timing.ExposureStartedUtc, descriptor.Timing.DurableIngressUtc),
                CancellationToken.None).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(template.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = CreatePersistence(template.Options, store, storage, telemetry);
            var raw = new ProcessingArtifact(descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw,
                descriptor.Artifact.Variant, ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
                descriptor.Artifact.MediaType, descriptor.Layout, pixels, descriptor.Artifact.CreatedUtc,
                descriptor.Controls.EffectiveExposure, CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor),
                descriptor.Capture.CaptureSequence, descriptor.Artifact.SourceArtifactIds,
                descriptor.Timing.ExposureStartedUtc, descriptor.Timing.ExposureEndedUtc)
            { CaptureId = captureId };
            var executor = new ProcessingRecipeExecutor();
            var combined = await executor.ExecuteAsync(new(BuiltInProcessingRecipes.RollingMean,
                JsonSerializer.SerializeToElement(new { maximumFrameCount = 1 }), ProcessingInputSelector.Raw(),
                [raw], "rolling-mean"), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, combined.Status, combined.ReasonCode);
            var combinedArtifact = ToArtifact(combined.Products.Single());
            var preview = await executor.ExecuteAsync(new(BuiltInProcessingRecipes.EncodedPreview,
                JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
                ProcessingInputSelector.Combined(), [combinedArtifact], "combined-preview"), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, preview.Status, preview.ReasonCode);
            var previewArtifact = ToArtifact(preview.Products.Single());
            var original = PresentationProcessingProducts.CreateManifestProduct(
                PresentationProcessingProducts.CreateReference(previewArtifact, new string('C', 64)), null, [],
                "overlay-manifest", previewArtifact);
            var graph = new CaptureProcessingGraph([
                Node("rolling", new FixedArtifactProducingStep(combined.Products.Single()), []),
                Node("combined-preview", new FixedArtifactProducingStep(preview.Products.Single()), ["rolling"]),
                Node("overlay-manifest", new FixedMetadataProducingStep(original), ["combined-preview"])
            ]);
            var processed = await FrameProcessingWorker.ProcessGraphItemAsync(item, graph, persistence, telemetry,
                1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Outcome, processed.Reason);
            using var artifacts = new CameraAgentArtifactService(template.Options, store, new CameraAgentPreviewEncoder());
            var source = new TransientSourceEvidenceReferenceV1(TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
                Guid.NewGuid(), new(TransientWholeArtifactLocatorV1.CurrentSchemaVersion, TransientSourceLocatorKind.WholeArtifact,
                    new(raw.ArtifactId, raw.Role, raw.Variant, raw.RecipeIdentitySha256, descriptor.Artifact.ChecksumSha256)),
                raw.ObservationStartedUtc!.Value, raw.ObservationEndedUtc!.Value, TransientTimingQuality.Reported,
                new("raw-ingress-manifest", ArtifactManifestV2.CurrentSchemaVersion));
            var detector = TransientDetectorInputFactory.Create(raw, source,
                new((ushort)descriptor.Layout.BlackLevel!.Value, (ushort)descriptor.Layout.WhiteLevel!.Value,
                    (ushort)descriptor.Layout.WhiteLevel!.Value)).Input!;
            var trail = new CameraAgentCaptureTrail(Guid.NewGuid(), Guid.NewGuid(), "ProvisionalCandidate",
                new string('A', 64), new string('B', 64), null, null, manifest, detector.Descriptor,
                new(source.EvidenceId, width, height, new(10, 10, 100, 100), [new(10, 10), new(110, 110)]));
            var evidence = new CameraAgentCaptureTrails(captureId, "NoRecordedCandidate", [], false);
            var projection = new Mock<ICameraAgentTransientOperatorProjection>(MockBehavior.Strict);
            projection.Setup(value => value.GetCaptureTrailsAsync(captureId, It.IsAny<CancellationToken>()))
                .Returns(() => ValueTask.FromResult(evidence));
            var baselineService = new CameraAgentLayeredPresentationService(store, artifacts);
            var optionalService = new CameraAgentLayeredPresentationService(store, artifacts, projection.Object, persistence);
            var baseline = await MeasureAsync(baselineService).ConfigureAwait(false);
            var none = await MeasureAsync(optionalService).ConfigureAwait(false);
            var baselineConcurrent = new List<object>();
            var noneConcurrent = new List<object>();
            foreach (var readers in new[] { 4, 8 })
            {
                baselineConcurrent.Add(await MeasureAsync(baselineService, readers).ConfigureAwait(false));
                noneConcurrent.Add(await MeasureAsync(optionalService, readers).ConfigureAwait(false));
            }
            evidence = evidence with { State = "MeasuredEvidence", Trails = [trail] };
            var filesBefore = OutputFootprint();
            var publicationStart = Stopwatch.GetTimestamp();
            var first = await optionalService.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            var publicationMilliseconds = Stopwatch.GetElapsedTime(publicationStart).TotalMilliseconds;
            Assert.IsNotNull(first.Presentation);
            Assert.AreEqual(width, first.Presentation.WidthPixels);
            Assert.AreEqual(height, first.Presentation.HeightPixels);
            var layer = first.Presentation.Layers.Single();
            Assert.AreEqual(TransientTrailPresentation.LayerKind, layer.Kind);
            Assert.IsFalse(layer.EnabledByDefault);
            var measured = await MeasureAsync(optionalService).ConfigureAwait(false);
            var concurrent = new List<object>();
            foreach (var readers in new[] { 4, 8 })
            {
                concurrent.Add(await MeasureAsync(optionalService, readers).ConfigureAwait(false));
            }
            Assert.AreEqual(1, (await store.ReadCaptureProductsAsync(captureId, CaptureProcessingPersistence.TrailCompositionSchema,
                2, CancellationToken.None).ConfigureAwait(false)).Count);
            var materializer = new CameraAgentPresentationMaterializer(store, persistence, optionalService);
            var saveStart = Stopwatch.GetTimestamp();
            var saved = await materializer.SaveFrozenAsync(captureId, first.Presentation.ManifestIdentitySha256,
                [layer.IdentitySha256], "issue-1005-performance", CancellationToken.None).ConfigureAwait(false);
            var saveMilliseconds = Stopwatch.GetElapsedTime(saveStart).TotalMilliseconds;
            Assert.AreEqual(CameraAgentPresentationMaterializationStatus.Saved, saved.Status, saved.Reason);
            var replay = await materializer.SaveFrozenAsync(captureId, first.Presentation.ManifestIdentitySha256,
                [layer.IdentitySha256], "issue-1005-performance", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(saved.Receipt!.ArtifactId, replay.Receipt!.ArtifactId);
            evidence = evidence with { Trails = [trail with { State = "ConfirmedMeteor", FinalizationIdentitySha256 = new string('D', 64) }] };
            var changed = await optionalService.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreNotEqual(first.Presentation.ManifestArtifactId, changed.Presentation!.ManifestArtifactId);
            var stale = await materializer.SaveFrozenAsync(captureId, first.Presentation.ManifestIdentitySha256,
                [layer.IdentitySha256], "issue-1005-performance", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentPresentationMaterializationStatus.Conflict, stale.Status);
            var savedOutput = await store.ReadOutputByArtifactIdAsync(saved.Receipt.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(savedOutput);
            var savedBytes = await File.ReadAllBytesAsync(Path.Combine(root, savedOutput.PayloadRelativePath)).ConfigureAwait(false);
            Assert.AreEqual(saved.Receipt.ChecksumSha256, ProcessingIdentity.ComputePayloadSha256(savedBytes));
            var evidencePath = Environment.GetEnvironmentVariable("HVO_ISSUE1005_EVIDENCE_PATH");
            if (!string.IsNullOrWhiteSpace(evidencePath))
            {
                await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(new
                {
                    schemaVersion = "issue-1005-measured-trail-performance-v1",
                    measuredUtc = DateTimeOffset.UtcNow,
                    revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
                    workload = "W1",
                    baselineDefinition = "Same binary, optional projection and composition dependencies absent; original presentation path.",
                    candidateDefinition = "Same binary with optional projection and durable composition enabled.",
                    limitation = "Projection is a bounded deterministic stub; measures composition overhead, not detector or SQLite candidate-query throughput. Real capture projection and browser acceptance run separately.",
                    width,
                    height,
                    rawBytes = pixels.Length,
                    baseline,
                    baselineConcurrent,
                    none,
                    noneConcurrent,
                    measured,
                    concurrent,
                    publicationMilliseconds,
                    saveMilliseconds,
                    filesBefore,
                    filesAfter = OutputFootprint(),
                    first.Presentation.ManifestIdentitySha256,
                    first.Presentation.SvgChecksumSha256,
                    saved.Receipt,
                    retainedSavedChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(savedBytes),
                    processorCount = Environment.ProcessorCount,
                    peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64
                }, Issue434JsonOptions)).ConfigureAwait(false);
            }

            ProcessingArtifact ToArtifact(ProcessingProduct product) => new(
                ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), product.Role, product.Variant,
                product.Recipe.IdentitySha256, product.MediaType, product.Layout, product.Payload, raw.CreatedUtc,
                product.TotalIntegration, product.Compatibility, raw.CaptureSequence, product.SourceArtifactIds,
                raw.ObservationStartedUtc, raw.ObservationEndedUtc)
            { CaptureId = captureId };

            object OutputFootprint()
            {
                var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(path => !path.Contains(Path.DirectorySeparatorChar + "journal" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    .Select(path => new FileInfo(path)).ToArray();
                return new { files = files.Length, bytes = files.Sum(file => file.Length) };
            }

            async Task<object> MeasureAsync(CameraAgentLayeredPresentationService service, int readers = 1)
            {
                var firstReadStart = Stopwatch.GetTimestamp();
                Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found,
                    (await service.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false)).Status);
                var firstReadMilliseconds = Stopwatch.GetElapsedTime(firstReadStart).TotalMilliseconds;
                for (var warmup = 0; warmup < 20; warmup++)
                    Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found,
                        (await service.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false)).Status);
                var values = new double[200];
                var process = Process.GetCurrentProcess();
                var cpuStart = process.TotalProcessorTime;
                var allocatedStart = GC.GetTotalAllocatedBytes(true);
                var measurementStart = Stopwatch.GetTimestamp();
                for (var batch = 0; batch < values.Length; batch += readers)
                {
                    await Task.WhenAll(Enumerable.Range(batch, Math.Min(readers, values.Length - batch))
                        .Select(async index =>
                        {
                            var started = Stopwatch.GetTimestamp();
                            var result = await service.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
                            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, result.Status);
                            Assert.IsNotNull(result.Presentation);
                            values[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        })).ConfigureAwait(false);
                }
                var elapsedMilliseconds = Stopwatch.GetElapsedTime(measurementStart).TotalMilliseconds;
                return new
                {
                    measurements = values.Length,
                    concurrency = readers,
                    warmups = 20,
                    elapsedMilliseconds,
                    readsPerSecond = values.Length * 1000 / elapsedMilliseconds,
                    firstReadMilliseconds,
                    medianMilliseconds = Percentile(values, .5),
                    p95Milliseconds = Percentile(values, .95),
                    maximumMilliseconds = values.Max(),
                    allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocatedStart,
                    cpuMilliseconds = (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
                    workingSetBytes = process.WorkingSet64
                };
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }

        static CaptureProcessingGraphNode Node(string id, ICaptureProcessingStep step, IReadOnlyList<string> dependencies)
        {
            var graphStep = (ICaptureProcessingGraphStep)step;
            return new(id, step, dependencies, true, graphStep.RecipeName, graphStep.OutputRole,
                graphStep.OutputVariant, CaptureContractJson.ComputeCanonicalJsonSha256(new { id }));
        }
    }
}
