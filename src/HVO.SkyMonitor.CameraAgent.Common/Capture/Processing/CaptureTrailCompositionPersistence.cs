using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed record CaptureTrailComposition(
    Guid ManifestArtifactId, CameraAgentCaptureTrails Evidence);

internal sealed partial class CaptureProcessingPersistence
{
    internal const string TrailCompositionSchema = TransientTrailPresentation.CompositionSchemaVersion;
    private const string TrailEvidenceSchema = "capture-trail-evidence-v1";

    internal async ValueTask<CaptureTrailComposition> PublishTrailCompositionAsync(
        Guid captureId, Guid originalManifestArtifactId, CameraAgentCaptureTrails evidence,
        CancellationToken cancellationToken)
    {
        if (evidence.CaptureId != captureId || evidence.Trails.Count == 0 || evidence.Incomplete)
        {
            return new(originalManifestArtifactId, evidence);
        }
        var gate = StorageLifecycleLock.ForRoot(_storageRoot);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var originalOutput = await RequireOutputAsync(originalManifestArtifactId, captureId, cancellationToken).ConfigureAwait(false);
            var original = await RestoreOutputAsync(originalOutput, cancellationToken).ConfigureAwait(false);
            var manifest = LayeredPresentationJson.ParseManifest(original.Product.Payload).Document
                ?? throw new InvalidDataException("The retained presentation manifest is invalid.");
            var baseOutput = await RequireOutputAsync(manifest.BaseProduct.ArtifactId, captureId, cancellationToken).ConfigureAwait(false);
            if (baseOutput.Descriptor is not { } descriptor)
            {
                return Unsupported("The retained presentation base is not reconstructable.");
            }
            var source = evidence.Trails[0].SourceManifest.Descriptor;
            // Exact membership and a supported immutable producer chain establish the mapping, never dimensions alone.
            if (descriptor.Layout.Width != source.Layout.Width || descriptor.Layout.Height != source.Layout.Height ||
                descriptor.Timing.ExposureStartedUtc != source.Timing.ExposureStartedUtc ||
                descriptor.Timing.ExposureEndedUtc != source.Timing.ExposureEndedUtc ||
                !await HasSourcePixelMappingAsync(baseOutput, source, _store.ReadOutputByArtifactIdAsync, cancellationToken).ConfigureAwait(false) ||
                evidence.Trails.Any(trail => trail.SourceManifest.Descriptor != source))
            {
                return Unsupported("Measured trails do not have an exact source/exposure mapping to this presentation base.");
            }
            var snapshot = CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(new
            {
                SchemaVersion = TrailEvidenceSchema,
                captureId,
                OriginalManifestIdentitySha256 = manifest.ManifestIdentitySha256,
                SourceManifestIdentitySha256 = CaptureContractJson.ComputeManifestSha256(evidence.Trails[0].SourceManifest),
                PositionalUncertainty = "Unavailable",
                Trails = evidence.Trails.Select(static trail => new
                {
                    trail.CandidateId,
                    trail.EventId,
                    trail.State,
                    trail.CandidateIdentitySha256,
                    trail.ExtractionIdentitySha256,
                    trail.AssessmentIdentitySha256,
                    trail.FinalizationIdentitySha256,
                    trail.DetectorInput,
                    trail.Geometry
                }).ToArray()
            }));
            var snapshotBytes = Encoding.UTF8.GetBytes(snapshot.GetRawText());
            if (snapshotBytes.Length > 1024 * 1024) return Unsupported("Measured trail evidence exceeds its metadata bound.");
            var snapshotIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(snapshot);
            var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
                "capture-trail-evidence", "1.0.0", TrailEvidenceSchema,
                JsonSerializer.SerializeToElement(new { snapshotIdentity })));
            var sourceIds = new[] { source.Artifact.ArtifactId, originalManifestArtifactId, baseOutput.ArtifactId }.Distinct().ToArray();
            var snapshotProduct = new ProcessingProduct(
                FrameArtifactRole.Metadata, TrailEvidenceSchema,
                ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, TrailEvidenceSchema, recipe.IdentitySha256, sourceIds),
                "application/vnd.hvo.capture-trail-evidence+json", null, snapshotBytes,
                ProcessingIdentity.ComputePayloadSha256(snapshotBytes), recipe, [new("capture-trail-evidence", TrailEvidenceSchema)],
                sourceIds, TimeSpan.Zero, baseOutput.Compatibility)
            {
                Kind = ProcessingProductKind.Metadata,
                SchemaVersion = TrailEvidenceSchema,
                ContentIdentitySha256 = snapshotIdentity
            };
            var snapshotArtifact = ToArtifact(snapshotProduct);
            var payload = TransientTrailPresentation.CreatePayload(snapshotIdentity, source.Layout.Width, source.Layout.Height,
                evidence.Trails.Select(static trail => new TransientMeasuredTrail(
                    trail.Geometry, trail.DetectorInput, trail.State == "ConfirmedMeteor")).ToArray());
            var trailProduct = PresentationProcessingProducts.CreateLayerProduct(payload,
                TransientTrailPresentation.ProducerVersion, [snapshotArtifact], TransientTrailPresentation.ProducerVersion);
            var trailArtifact = ToArtifact(trailProduct);
            var trailReference = new PresentationProductReference(trailArtifact.ArtifactId,
                payload.ContentIdentitySha256, trailArtifact.MediaType, manifest.BaseProduct.Compatibility);
            var layer = LayeredPresentationJson.CreateLayer(TransientTrailPresentation.LayerKind,
                trailReference, manifest.SceneIdentitySha256, PresentationCoordinateSpace.ScenePixels,
                PresentationLayerCompositor.AlgorithmVersion, TransientTrailPresentation.ProducerVersion,
                500, PresentationBlendMode.Normal, 1_000_000, false,
                JsonSerializer.SerializeToElement(new { EvidenceIdentitySha256 = snapshotIdentity, PositionalUncertainty = "Unavailable" }));
            var expectedManifest = LayeredPresentationJson.CreateManifest(manifest.BaseProduct,
                manifest.SceneIdentitySha256, manifest.Layers.Append(layer));
            var expectedRecipe = PresentationProcessingProducts.CreateManifestRecipeIdentity(expectedManifest);
            var expectedSources = new[] { expectedManifest.BaseProduct.ArtifactId }
                .Concat(expectedManifest.Layers.Select(static value => value.SourceProduct.ArtifactId)).ToArray();
            var expectedOutputIdentity = ProcessingIdentity.CreateOutputIdentity(
                FrameArtifactRole.Metadata, TrailCompositionSchema, expectedRecipe.IdentitySha256, expectedSources);
            var expectedArtifactId = ProcessingIdentity.CreateArtifactId(expectedOutputIdentity);
            if (await _store.ReadOutputByArtifactIdAsync(expectedArtifactId, cancellationToken).ConfigureAwait(false) is { } retained)
            {
                var restored = await RestoreOutputAsync(retained, cancellationToken).ConfigureAwait(false);
                if (retained.Capture.CaptureId != captureId || restored.Product.SchemaVersion != TrailCompositionSchema ||
                    restored.Product.ContentIdentitySha256 != expectedManifest.ManifestIdentitySha256 ||
                    restored.Product.ChecksumSha256 != ProcessingIdentity.ComputePayloadSha256(LayeredPresentationJson.Serialize(expectedManifest)))
                    throw new InvalidDataException("The measured composition conflicts with retained evidence.");
                foreach (var expectedProduct in new[] { snapshotProduct, trailProduct })
                {
                    var expectedId = ProcessingIdentity.CreateArtifactId(expectedProduct.OutputIdentitySha256);
                    var retainedEvidence = await RequireOutputAsync(expectedId, captureId, cancellationToken).ConfigureAwait(false);
                    var restoredEvidence = await RestoreOutputAsync(retainedEvidence, cancellationToken).ConfigureAwait(false);
                    if (restoredEvidence.Product.SchemaVersion != expectedProduct.SchemaVersion ||
                        restoredEvidence.Product.ContentIdentitySha256 != expectedProduct.ContentIdentitySha256 ||
                        restoredEvidence.Product.ChecksumSha256 != expectedProduct.ChecksumSha256 ||
                        !restoredEvidence.Product.SourceArtifactIds.SequenceEqual(expectedProduct.SourceArtifactIds))
                        throw new InvalidDataException("The measured layer conflicts with its retained evidence.");
                }
                return new(expectedArtifactId, evidence);
            }
            // A new publication validates the raster and every layer once. An unchanged composition above stays metadata-only.
            var restoredBase = await RestoreOutputAsync(baseOutput, cancellationToken).ConfigureAwait(false);
            if (restoredBase.Artifact is null) return Unsupported("The retained presentation base is not reconstructable.");
            var baseArtifact = CreateProcessingArtifact(baseOutput, restoredBase);
            var originalArtifact = CreateProcessingArtifact(originalOutput, original);
            var layerInputs = new List<PresentationLayerProductInput>(manifest.Layers.Count + 1);
            foreach (var retainedLayer in manifest.Layers)
            {
                var output = await RequireOutputAsync(retainedLayer.SourceProduct.ArtifactId, captureId, cancellationToken).ConfigureAwait(false);
                var restored = await RestoreOutputAsync(output, cancellationToken).ConfigureAwait(false);
                layerInputs.Add(new(retainedLayer, CreateProcessingArtifact(output, restored)));
            }
            layerInputs.Add(new(layer, trailArtifact));
            var composed = PresentationProcessingProducts.CreateManifestProduct(manifest.BaseProduct,
                manifest.SceneIdentitySha256, layerInputs, TrailCompositionSchema, baseArtifact) with
            {
                // The wire manifest remains V1; this distinct durable product schema leaves the original selector unique.
                SchemaVersion = TrailCompositionSchema
            };
            var composedArtifactId = ProcessingIdentity.CreateArtifactId(composed.OutputIdentitySha256);
            if (composedArtifactId != expectedArtifactId)
                throw new InvalidDataException("The measured composition does not reproduce its canonical product identity.");
            var publicationNodeId = "capture-trails-" + composed.OutputIdentitySha256[..16];
            var outputs = new List<DurableProcessingOutput>(3);
            foreach (var product in new[] { snapshotProduct, trailProduct, composed })
            {
                outputs.Add(await PersistMetadataProductAsync(_storageRoot, descriptor,
                    publicationNodeId, product, cancellationToken).ConfigureAwait(false));
            }
            var now = _timeProvider.GetUtcNow();
            var node = new CaptureProcessingGraphNode(publicationNodeId,
                OnDemandMaterializationStep.Instance, [], false, "capture-trail-composition",
                FrameArtifactRole.Metadata, TrailCompositionSchema, composed.OutputIdentitySha256);
            var inputs = new[]
            {
                new DurableProcessingNodeInput(0, "Artifact", "base", baseArtifact.ArtifactId, baseArtifact.Role,
                    baseArtifact.Variant, baseArtifact.RecipeIdentitySha256, baseArtifact.SchemaVersion, baseArtifact.ContentIdentitySha256, true),
                new DurableProcessingNodeInput(1, "Artifact", "original-manifest", originalArtifact.ArtifactId, originalArtifact.Role,
                    originalArtifact.Variant, originalArtifact.RecipeIdentitySha256, originalArtifact.SchemaVersion, originalArtifact.ContentIdentitySha256, true),
                new DurableProcessingNodeInput(2, "Artifact", "measured-source", source.Artifact.ArtifactId, source.Artifact.Role,
                    source.Artifact.Variant, ProcessingIdentity.CreateRecipeIdentity(source.Artifact.Recipe).IdentitySha256,
                    ArtifactManifestV2.CurrentSchemaVersion, source.Artifact.ChecksumSha256, true)
            };
            await _store.WriteNodeAsync(captureId, node, DurableProcessingNodeStatus.Completed, null,
                1, null, now, now, TimeSpan.Zero, ProcessingOutcomeStatus.Produced, inputs, 0, null,
                outputs, null, ProcessingNodeExecutionRoute.Unknown, cancellationToken).ConfigureAwait(false);
            return new(composedArtifactId, evidence);

            ProcessingArtifact ToArtifact(ProcessingProduct product) => new(
                ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), product.Role, product.Variant,
                product.Recipe.IdentitySha256, product.MediaType, product.Layout, product.Payload,
                source.Artifact.CreatedUtc, product.TotalIntegration, product.Compatibility,
                source.Capture.CaptureSequence, product.SourceArtifactIds)
            {
                CaptureId = captureId,
                ProductKind = product.Kind,
                SchemaVersion = product.SchemaVersion,
                ContentIdentitySha256 = product.ContentIdentitySha256
            };
        }
        finally
        {
            gate.Release();
        }

        CaptureTrailComposition Unsupported(string reason) => new(originalManifestArtifactId,
            evidence with { State = "Unavailable", Incomplete = true, Reason = reason });
    }

    internal const int MaximumLineageHops = 8;

    internal static ValueTask<bool> HasSourcePixelMappingAsync(DurableProcessingOutput output,
        ReconstructionDescriptor source, Func<Guid, CancellationToken, ValueTask<DurableProcessingOutput?>> readOutput,
        CancellationToken cancellationToken) =>
        HasSourcePixelMappingAsync(output, source, readOutput, [], MaximumLineageHops, cancellationToken);

    // Every source edge, including the final edge into the raw source, consumes one hop of the budget.
    private static async ValueTask<bool> HasSourcePixelMappingAsync(DurableProcessingOutput output,
        ReconstructionDescriptor source, Func<Guid, CancellationToken, ValueTask<DurableProcessingOutput?>> readOutput,
        HashSet<Guid> visited, int remainingHops, CancellationToken cancellationToken)
    {
        if (!visited.Add(output.ArtifactId) || output.AvailabilityState != "Available" ||
            output.Descriptor is not { } descriptor || descriptor.Capture.CaptureId != source.Capture.CaptureId ||
            descriptor.Layout.Width != source.Layout.Width || descriptor.Layout.Height != source.Layout.Height ||
            descriptor.Timing.ExposureStartedUtc != source.Timing.ExposureStartedUtc ||
            descriptor.Timing.ExposureEndedUtc != source.Timing.ExposureEndedUtc)
        {
            return false;
        }
        var expected = CameraAgentRecipeExecutionAdapter.CreateCompatibility(source);
        if (output.Compatibility.Rig != expected.Rig || output.Compatibility.Orientation != expected.Orientation ||
            output.Compatibility.Sensor != expected.Sensor || output.Compatibility.LocationIdentitySha256 != expected.LocationIdentitySha256)
        {
            return false;
        }
        if (output.ArtifactId == source.Artifact.ArtifactId)
            return output.Artifact.ChecksumSha256 == source.Artifact.ChecksumSha256;
        if (remainingHops < 1 || !TransientTrailPresentation.PreservesSourcePixels(output.Artifact.Role, output.Artifact.Recipe, output.Algorithms)) return false;
        var ids = descriptor.Artifact.SourceArtifactIds;
        if (ids.Count is < 1 or > 100 || ids.Distinct().Count() != ids.Count) return false;
        if (output.Artifact.Recipe.Name is BuiltInProcessingRecipes.FixedPreview or BuiltInProcessingRecipes.EncodedPreview or
            BuiltInProcessingRecipes.LinearNormalization && ids.Count != 1)
            return false;
        // Calibration's first canonical source is the science exposure; master references cannot establish its geometry.
        var candidates = output.Artifact.Recipe.Name == BuiltInProcessingRecipes.ReferenceCalibration ? ids.Take(1) : ids;
        foreach (var id in candidates)
        {
            if (id == source.Artifact.ArtifactId) return true;
            var parent = await readOutput(id, cancellationToken).ConfigureAwait(false);
            if (parent is not null && await HasSourcePixelMappingAsync(parent, source, readOutput, visited, remainingHops - 1,
                cancellationToken).ConfigureAwait(false))
                return true;
        }
        return false;
    }
}
