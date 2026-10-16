using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Moq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Common.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task CaptureTrailComposition_PreservesOriginalManifestAndFreezesRasterLineageAcrossEvidenceChanges()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var descriptor = fixture.Manifest.Descriptor;
            var captureId = descriptor.Capture.CaptureId;
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 5);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await journal.ReserveIdentityAsync(descriptor.Capture.AgentId,
                captureId, descriptor.Artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            var manifestBytes = CaptureContractJson.Serialize(fixture.Manifest);
            await journal.CommitAsync(new RawIngressJournalEntry(
                descriptor.Capture.AgentId, descriptor.Capture.CaptureSequence, captureId, descriptor.Artifact.ArtifactId,
                CaptureContractJson.ComputeDescriptorSha256(descriptor), CaptureContractJson.ComputeManifestSha256(manifestBytes),
                descriptor.Artifact.ChecksumSha256, descriptor.Layout.ByteLength, fixture.Manifest.RelativeArtifactPath,
                Path.ChangeExtension(fixture.Manifest.RelativeArtifactPath, ".json"), manifestBytes,
                descriptor.Timing.ExposureStartedUtc, descriptor.Timing.DurableIngressUtc), CancellationToken.None).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = CreatePersistence(fixture.Options, store, storage, telemetry);
            var raw = new ProcessingArtifact(descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw,
                descriptor.Artifact.Variant, ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
                descriptor.Artifact.MediaType, descriptor.Layout, await File.ReadAllBytesAsync(Path.Combine(root, "raw.bin")).ConfigureAwait(false),
                descriptor.Artifact.CreatedUtc, descriptor.Controls.EffectiveExposure,
                CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor), descriptor.Capture.CaptureSequence,
                descriptor.Artifact.SourceArtifactIds, descriptor.Timing.ExposureStartedUtc, descriptor.Timing.ExposureEndedUtc);
            var executor = new ProcessingRecipeExecutor();
            var combined = await executor.ExecuteAsync(new ProcessingExecutionRequest(
                BuiltInProcessingRecipes.RollingMean, JsonSerializer.SerializeToElement(new { maximumFrameCount = 1 }),
                ProcessingInputSelector.Raw(), [raw], "rolling-mean"), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, combined.Status, combined.ReasonCode);
            var combinedProduct = combined.Products.Single();
            var combinedArtifact = new ProcessingArtifact(ProcessingIdentity.CreateArtifactId(combinedProduct.OutputIdentitySha256),
                combinedProduct.Role, combinedProduct.Variant, combinedProduct.Recipe.IdentitySha256,
                combinedProduct.MediaType, combinedProduct.Layout, combinedProduct.Payload, raw.CreatedUtc,
                combinedProduct.TotalIntegration, combinedProduct.Compatibility, raw.CaptureSequence,
                combinedProduct.SourceArtifactIds, raw.ObservationStartedUtc, raw.ObservationEndedUtc)
            { CaptureId = captureId };
            var preview = await executor.ExecuteAsync(new ProcessingExecutionRequest(
                BuiltInProcessingRecipes.EncodedPreview, JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
                ProcessingInputSelector.Combined(), [combinedArtifact], "combined-preview"), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, preview.Status, preview.ReasonCode);
            var graph = await CreateProductionLayeredGraphAsync(fixture, root).ConfigureAwait(false);
            graph = new CaptureProcessingGraph(graph.Nodes.Select(node => node.Id switch
            {
                "rolling" => node with { Step = new FixedArtifactProducingStep(combinedProduct) },
                "combined-preview" => node with { Step = new FixedArtifactProducingStep(preview.Products.Single()), RecipeName = BuiltInProcessingRecipes.EncodedPreview },
                _ => node
            }).ToArray());

            using var artifacts = new CameraAgentArtifactService(fixture.Options, store, new CameraAgentPreviewEncoder());
            var missingService = new CameraAgentLayeredPresentationService(store, artifacts);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.NotRetained,
                (await missingService.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false)).Status);
            var workerLogger = new Mock<ILogger>();
            workerLogger.Setup(value => value.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            var processed = await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Item, graph, persistence, telemetry,
                1, workerLogger.Object, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Outcome,
                processed.Reason + string.Join("\n", workerLogger.Invocations.Select(value => string.Join(" ", value.Arguments))));
            var rollingOutput = await store.ReadOutputByArtifactIdAsync(combinedArtifact.ArtifactId, CancellationToken.None)
                .ConfigureAwait(false) ?? throw new AssertFailedException("The rolling mean output was not retained.");
            var previewOutput = await store.ReadOutputByArtifactIdAsync(
                ProcessingIdentity.CreateArtifactId(preview.Products.Single().OutputIdentitySha256), CancellationToken.None)
                .ConfigureAwait(false) ?? throw new AssertFailedException("The preview base output was not retained.");
            Assert.IsTrue(await CaptureProcessingPersistence.HasSourcePixelMappingAsync(previewOutput, descriptor,
                store.ReadOutputByArtifactIdAsync, CancellationToken.None).ConfigureAwait(false));
            static DurableProcessingOutput Relink(DurableProcessingOutput output, Guid artifactId, params Guid[] sourceArtifactIds)
            {
                var retained = output.Descriptor ?? throw new AssertFailedException("The retained output has no descriptor.");
                return output with
                {
                    ArtifactId = artifactId,
                    Descriptor = retained with
                    {
                        Artifact = retained.Artifact with { ArtifactId = artifactId, SourceArtifactIds = [.. sourceArtifactIds] }
                    }
                };
            }
            async ValueTask<bool> MapsThroughAsync(Guid baseSourceArtifactId, Dictionary<Guid, DurableProcessingOutput> lineage) =>
                await CaptureProcessingPersistence.HasSourcePixelMappingAsync(Relink(previewOutput, Guid.NewGuid(), baseSourceArtifactId),
                    descriptor, (id, _) => ValueTask.FromResult<DurableProcessingOutput?>(lineage.GetValueOrDefault(id)),
                    CancellationToken.None).ConfigureAwait(false);
            async ValueTask<bool> MapsThroughRollingChainAsync(int rollingCount)
            {
                var lineage = new Dictionary<Guid, DurableProcessingOutput>();
                var previous = descriptor.Artifact.ArtifactId;
                for (var index = 0; index < rollingCount; index++)
                {
                    var link = Relink(rollingOutput, Guid.NewGuid(), previous);
                    lineage.Add(link.ArtifactId, link);
                    previous = link.ArtifactId;
                }
                return await MapsThroughAsync(previous, lineage).ConfigureAwait(false);
            }
            async ValueTask<bool> MapsThroughSharedAncestorAsync(bool longBranchFirst)
            {
                var shared = Relink(rollingOutput, Guid.NewGuid(), descriptor.Artifact.ArtifactId);
                var lineage = new Dictionary<Guid, DurableProcessingOutput> { [shared.ArtifactId] = shared };
                var previous = shared.ArtifactId;
                for (var index = 0; index < CaptureProcessingPersistence.MaximumLineageHops - 2; index++)
                {
                    var link = Relink(rollingOutput, Guid.NewGuid(), previous);
                    lineage.Add(link.ArtifactId, link);
                    previous = link.ArtifactId;
                }
                var root = longBranchFirst
                    ? Relink(rollingOutput, Guid.NewGuid(), previous, shared.ArtifactId)
                    : Relink(rollingOutput, Guid.NewGuid(), shared.ArtifactId, previous);
                lineage.Add(root.ArtifactId, root);
                return await MapsThroughAsync(root.ArtifactId, lineage).ConfigureAwait(false);
            }
            // R1005-1-F2: the preview base over N rolling means is N + 1 source edges, the last one into the raw source.
            Assert.IsTrue(await MapsThroughRollingChainAsync(CaptureProcessingPersistence.MaximumLineageHops - 1).ConfigureAwait(false));
            Assert.IsFalse(await MapsThroughRollingChainAsync(CaptureProcessingPersistence.MaximumLineageHops).ConfigureAwait(false));
            // R1005-2-F1: exhausting the nine-edge branch must not hide the three-edge route through the shared ancestor.
            Assert.IsTrue(await MapsThroughSharedAncestorAsync(longBranchFirst: true).ConfigureAwait(false));
            Assert.IsTrue(await MapsThroughSharedAncestorAsync(longBranchFirst: false).ConfigureAwait(false));
            var (cycleStart, cycleEnd) = (Guid.NewGuid(), Guid.NewGuid());
            Assert.IsFalse(await MapsThroughAsync(cycleStart, new()
            {
                [cycleStart] = Relink(rollingOutput, cycleStart, cycleEnd),
                [cycleEnd] = Relink(rollingOutput, cycleEnd, cycleStart)
            }).ConfigureAwait(false));
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found,
                (await missingService.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false)).Status);
            var originals = await store.ReadCaptureProductsAsync(captureId, OverlayManifestV1.CurrentSchemaVersion, 2,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, originals.Count);
            var originalOutput = await store.ReadOutputByArtifactIdAsync(originals[0].ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(originalOutput);
            var originalBytes = await File.ReadAllBytesAsync(Path.Combine(root, originalOutput.PayloadRelativePath)).ConfigureAwait(false);

            var source = new TransientSourceEvidenceReferenceV1(TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
                Guid.NewGuid(), new(TransientWholeArtifactLocatorV1.CurrentSchemaVersion, TransientSourceLocatorKind.WholeArtifact,
                    new(raw.ArtifactId, raw.Role, raw.Variant, raw.RecipeIdentitySha256, descriptor.Artifact.ChecksumSha256)),
                descriptor.Timing.ExposureStartedUtc, descriptor.Timing.ExposureEndedUtc, TransientTimingQuality.Reported,
                new("raw-ingress-manifest", ArtifactManifestV2.CurrentSchemaVersion));
            var detector = TransientDetectorInputFactory.Create(raw, source,
                new((ushort)descriptor.Layout.BlackLevel!.Value, (ushort)descriptor.Layout.WhiteLevel!.Value,
                    (ushort)descriptor.Layout.WhiteLevel!.Value)).Input!;
            Assert.IsNotNull(detector);
            var measured = new CameraAgentCaptureTrail(Guid.NewGuid(), Guid.NewGuid(), "ProvisionalCandidate",
                new string('A', 64), new string('B', 64), null, null, fixture.Manifest, detector.Descriptor,
                new(source.EvidenceId, 2, 2, new(0, 0, 2, 2), [new(0.5, 0.5), new(1.5, 1.5)]));
            var evidence = new CameraAgentCaptureTrails(captureId, "MeasuredEvidence", [measured], false);
            var projection = new Mock<ICameraAgentTransientOperatorProjection>(MockBehavior.Strict);
            TaskCompletionSource? holdNextProjection = null;
            var heldProjection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async ValueTask<CameraAgentCaptureTrails> ProjectAsync()
            {
                var snapshot = evidence;
                if (Interlocked.Exchange(ref holdNextProjection, null) is { } release)
                {
                    heldProjection.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                }
                return snapshot;
            }
            projection.Setup(value => value.GetCaptureTrailsAsync(captureId, It.IsAny<CancellationToken>()))
                .Returns(() => ProjectAsync());
            var disabledProjection = new Mock<ICameraAgentTransientOperatorProjection>(MockBehavior.Strict);
            var disabledService = new CameraAgentLayeredPresentationService(store, artifacts,
                disabledProjection.Object, persistence, Options.Create(new CameraAgentHostOptions
                {
                    ArtifactRead = new() { MeasuredTransientTrailsEnabled = false }
                }));
            var disabled = await disabledService.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, disabled.Status);
            Assert.AreEqual(originals[0].ArtifactId, disabled.Presentation!.ManifestArtifactId);
            Assert.AreEqual("Disabled", disabled.Presentation.TrailEvidence!.State);
            disabledProjection.VerifyNoOtherCalls();
            Assert.AreEqual(0, (await store.ReadCaptureProductsAsync(captureId,
                CaptureProcessingPersistence.TrailCompositionSchema, 2, CancellationToken.None).ConfigureAwait(false)).Count);
            var presentations = new CameraAgentLayeredPresentationService(store, artifacts, projection.Object, persistence);
            evidence = evidence with
            {
                Trails = [measured with
                {
                    DetectorInput = detector.Descriptor with
                    {
                        SourceToDetectorTransform = detector.Descriptor.SourceToDetectorTransform with { Version = "unknown-transform-version" }
                    }
                }]
            };
            var unsupported = await presentations.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, unsupported.Status, unsupported.Reason);
            Assert.IsNotNull(unsupported.Presentation);
            Assert.AreEqual(originals[0].ArtifactId, unsupported.Presentation.ManifestArtifactId);
            Assert.AreEqual("Unavailable", unsupported.Presentation.TrailEvidence!.State);
            Assert.IsFalse(unsupported.Presentation.Layers.Any(layer => layer.Kind == TransientTrailPresentation.LayerKind));
            evidence = evidence with { Trails = [measured] };
            using (var faultConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
                Pooling = false
            }.ToString()))
            {
                await faultConnection.OpenAsync().ConfigureAwait(false);
                using var fault = faultConnection.CreateCommand();
                fault.CommandText = """
                    CREATE TRIGGER reject_trail_publication BEFORE INSERT ON processing_outputs
                    WHEN NEW.product_schema_version = 'capture-trail-composition-v1'
                    BEGIN SELECT RAISE(ABORT, 'injected publication failure'); END;
                    """;
                await fault.ExecuteNonQueryAsync().ConfigureAwait(false);
                var interrupted = await presentations.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, interrupted.Status);
                Assert.AreEqual(originals[0].ArtifactId, interrupted.Presentation!.ManifestArtifactId);
                Assert.AreEqual("Unavailable", interrupted.Presentation.TrailEvidence!.State);
                Assert.AreEqual(0, (await store.ReadCaptureProductsAsync(captureId,
                    CaptureProcessingPersistence.TrailCompositionSchema, 2, CancellationToken.None).ConfigureAwait(false)).Count);
                fault.CommandText = "DROP TRIGGER reject_trail_publication;";
                await fault.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            // Retry must validate and reuse any already published immutable files after the journal transaction failed.
            var first = await presentations.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, first.Status, first.Reason);
            Assert.IsNotNull(first.Presentation);
            var trailLayer = first.Presentation.Layers.Single(layer => layer.Kind == TransientTrailPresentation.LayerKind);
            Assert.IsFalse(trailLayer.EnabledByDefault);
            Assert.AreNotEqual(originals[0].ArtifactId, first.Presentation.ManifestArtifactId);
            CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(Path.Combine(root, originalOutput.PayloadRelativePath)).ConfigureAwait(false));
            Assert.AreEqual(1, (await store.ReadCaptureProductsAsync(captureId, OverlayManifestV1.CurrentSchemaVersion, 2,
                CancellationToken.None).ConfigureAwait(false)).Count);
            var repeatedComposition = await persistence.PublishTrailCompositionAsync(captureId, originals[0].ArtifactId,
                evidence, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(first.Presentation.ManifestArtifactId, repeatedComposition.ManifestArtifactId, repeatedComposition.Evidence.Reason);
            var unchanged = await presentations.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(first.Presentation.ManifestArtifactId, unchanged.Presentation!.ManifestArtifactId);
            Assert.AreEqual(first.Presentation.SvgChecksumSha256, unchanged.Presentation.SvgChecksumSha256);
            Assert.AreEqual(1, (await store.ReadCaptureProductsAsync(captureId, CaptureProcessingPersistence.TrailCompositionSchema, 2,
                CancellationToken.None).ConfigureAwait(false)).Count);
            var recoveredPresentation = await new CameraAgentLayeredPresentationService(store, artifacts, projection.Object, persistence)
                .GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(first.Presentation.ManifestArtifactId, recoveredPresentation.Presentation!.ManifestArtifactId);
            CollectionAssert.AreEqual(first.Presentation.Svg.ToArray(), recoveredPresentation.Presentation.Svg.ToArray());

            var materializer = new CameraAgentPresentationMaterializer(store, persistence, presentations);
            var saved = await materializer.SaveFrozenAsync(captureId, first.Presentation.ManifestIdentitySha256,
                [trailLayer.IdentitySha256], "operator-test", CancellationToken.None).ConfigureAwait(false);
            var replay = await materializer.SaveFrozenAsync(captureId, first.Presentation.ManifestIdentitySha256,
                [trailLayer.IdentitySha256], "operator-test", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentPresentationMaterializationStatus.Saved, saved.Status, saved.Reason);
            Assert.IsNotNull(saved.Receipt);
            Assert.IsNotNull(replay.Receipt);
            Assert.IsFalse(saved.Receipt.Replayed);
            Assert.IsTrue(replay.Receipt.Replayed);
            Assert.AreEqual(saved.Receipt.ArtifactId, replay.Receipt.ArtifactId);
            var savedOutput = await store.ReadOutputByArtifactIdAsync(saved.Receipt.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(savedOutput);
            CollectionAssert.Contains(savedOutput.Artifact.SourceArtifactIds.ToArray(), first.Presentation.ManifestArtifactId);
            var savedBytes = await File.ReadAllBytesAsync(Path.Combine(root, savedOutput.PayloadRelativePath)).ConfigureAwait(false);

            // R1005-1-F1: a read that already projected the shown evidence and is still in flight must not validate a
            // save requested after newer evidence was committed.
            var releaseHeldProjection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            holdNextProjection = releaseHeldProjection;
            var inFlight = presentations.GetAsync(captureId, CancellationToken.None).AsTask();
            await heldProjection.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            evidence = evidence with { Trails = [measured with { State = "ConfirmedMeteor", FinalizationIdentitySha256 = new string('C', 64) }] };
            CameraAgentPresentationMaterializationResult interleaved;
            try
            {
                interleaved = await materializer.SaveFrozenAsync(captureId, first.Presentation.ManifestIdentitySha256,
                    [trailLayer.IdentitySha256], "operator-test", CancellationToken.None).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            finally
            {
                releaseHeldProjection.TrySetResult();
            }
            Assert.AreEqual(CameraAgentPresentationMaterializationStatus.Conflict, interleaved.Status, interleaved.Reason);
            var heldRead = await inFlight.ConfigureAwait(false);
            Assert.AreEqual(first.Presentation.ManifestIdentitySha256, heldRead.Presentation!.ManifestIdentitySha256);
            CollectionAssert.AreEqual(savedBytes, await File.ReadAllBytesAsync(Path.Combine(root, savedOutput.PayloadRelativePath)).ConfigureAwait(false));
            var stale = await materializer.SaveFrozenAsync(captureId, first.Presentation.ManifestIdentitySha256,
                [trailLayer.IdentitySha256], "operator-test", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentPresentationMaterializationStatus.Conflict, stale.Status);
            CollectionAssert.AreEqual(savedBytes, await File.ReadAllBytesAsync(Path.Combine(root, savedOutput.PayloadRelativePath)).ConfigureAwait(false));
            var updated = await presentations.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreNotEqual(first.Presentation.ManifestIdentitySha256, updated.Presentation!.ManifestIdentitySha256);
            Assert.AreEqual("ConfirmedMeteor", updated.Presentation.TrailEvidence!.Trails.Single().State);
            evidence = evidence with
            {
                Trails =
                [
                    evidence.Trails.Single(),
                    measured with
                    {
                        CandidateId = Guid.NewGuid(), EventId = Guid.NewGuid(), CandidateIdentitySha256 = new string('D', 64),
                        Geometry = measured.Geometry with { Polyline = [new(0.5, 1.5), new(1.5, 0.5)] }
                    }
                ]
            };
            var multiple = await presentations.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(multiple.Presentation);
            Assert.AreEqual(2, multiple.Presentation.TrailEvidence!.Trails.Count);
            Assert.AreEqual(1, multiple.Presentation.Layers.Count(layer => layer.Kind == TransientTrailPresentation.LayerKind));
            Assert.AreNotEqual(updated.Presentation.ManifestIdentitySha256, multiple.Presentation.ManifestIdentitySha256);
            var multipleManifestOutput = await store.ReadOutputByArtifactIdAsync(multiple.Presentation.ManifestArtifactId,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(multipleManifestOutput);
            var multipleManifest = LayeredPresentationJson.ParseManifest(await File.ReadAllBytesAsync(
                Path.Combine(root, multipleManifestOutput.PayloadRelativePath)).ConfigureAwait(false)).Document!;
            var multipleTrailOutput = await store.ReadOutputByArtifactIdAsync(multipleManifest.Layers.Single(
                layer => layer.LayerKind == TransientTrailPresentation.LayerKind).SourceProduct.ArtifactId,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(multipleTrailOutput);
            var multiplePayload = PresentationLayerPayloadJson.Parse(await File.ReadAllBytesAsync(
                Path.Combine(root, multipleTrailOutput.PayloadRelativePath)).ConfigureAwait(false)).Payload!;
            Assert.AreEqual(2, multiplePayload.Segments.Count);
            Assert.AreEqual(1, multiplePayload.Segments.Count(segment => segment.Stroke is null));
            Assert.AreEqual(1, multiplePayload.Segments.Count(segment => segment.Stroke is not null));
            Assert.AreEqual(0, multiplePayload.Ellipses.Count);
            await File.WriteAllTextAsync(Path.Combine(root, multipleTrailOutput.PayloadRelativePath), "{}").ConfigureAwait(false);
            var corrupted = await presentations.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, corrupted.Status, corrupted.Reason);
            Assert.AreEqual(originals[0].ArtifactId, corrupted.Presentation!.ManifestArtifactId);
            Assert.AreEqual("Unavailable", corrupted.Presentation.TrailEvidence!.State);
            Assert.IsFalse(corrupted.Presentation.Layers.Any(layer => layer.Kind == TransientTrailPresentation.LayerKind));
            projection.Setup(value => value.GetCaptureTrailsAsync(captureId, It.IsAny<CancellationToken>()))
                .Throws(new SqliteException("private fixture path must never escape", 5));
            var temporarilyUnavailable = await presentations.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, temporarilyUnavailable.Status);
            Assert.AreEqual(originals[0].ArtifactId, temporarilyUnavailable.Presentation!.ManifestArtifactId);
            Assert.AreEqual("Unavailable", temporarilyUnavailable.Presentation.TrailEvidence!.State);
            Assert.IsFalse(temporarilyUnavailable.Presentation.TrailEvidence.Reason!.Contains("private fixture", StringComparison.Ordinal));
            CollectionAssert.AreEqual(savedBytes, await File.ReadAllBytesAsync(Path.Combine(root, savedOutput.PayloadRelativePath)).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
