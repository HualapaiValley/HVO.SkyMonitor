using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class CaptureProcessingPersistence
{
    // Uses the existing typed-product publisher and immutable revision/export model; no new geometry store.
    internal async ValueTask<(Guid ManifestArtifactId, CameraAgentRetainedAstrometry Evidence)> PublishAstrometricCompositionAsync(
        Guid captureId, Guid manifestArtifactId, CameraAgentRetainedAstrometry evidence, CancellationToken cancellationToken)
    {
        evidence = evidence with { CompatibleBaseArtifactId = null, DiagnosticsReason = "Compatible accepted source evidence is unavailable." };
        if (evidence.CaptureId != captureId || evidence.Diagnostics is not { HasMeasuredMapping: true } diagnostics ||
            evidence.SourceDescriptor is not { } source || evidence.ArtifactId is not { } solutionId ||
            evidence.Solution is not { } solution || !solution.Assessment.HasMeasuredMapping ||
            solution.Assessment.Frame.SourceArtifactId != source.Artifact.ArtifactId ||
            solution.Assessment.Frame.SourceDescriptorSha256 != CaptureContractJson.ComputeDescriptorSha256(source) ||
            diagnostics.AssessmentIdentitySha256 != solution.Assessment.IdentitySha256)
            return (manifestArtifactId, evidence);
        var gate = StorageLifecycleLock.ForRoot(_storageRoot);
        await StorageLifecycleLock.WaitAsync(gate, "astrometric-composition", _logger, cancellationToken).ConfigureAwait(false);
        try
        {
            var originalOutput = await RequireOutputAsync(manifestArtifactId, captureId, cancellationToken).ConfigureAwait(false);
            var original = await RestoreOutputAsync(originalOutput, cancellationToken).ConfigureAwait(false);
            var manifest = LayeredPresentationJson.ParseManifest(original.Product.Payload).Document
                ?? throw new InvalidDataException("Invalid retained manifest.");
            var baseOutput = await RequireOutputAsync(manifest.BaseProduct.ArtifactId, captureId, cancellationToken).ConfigureAwait(false);
            if (baseOutput.Descriptor is not { } descriptor ||
                !await HasSingleExposureAstrometricMappingAsync(baseOutput, source, _store.ReadOutputByArtifactIdAsync, cancellationToken).ConfigureAwait(false))
                return (manifestArtifactId, evidence with { CompatibleBaseArtifactId = null, DiagnosticsReason = "The displayed base has no exact source/exposure mapping to the measured result." });
            var solutionOutput = await RequireOutputAsync(solutionId, captureId, cancellationToken).ConfigureAwait(false);
            var restoredSolution = await RestoreOutputAsync(solutionOutput, cancellationToken).ConfigureAwait(false);
            if (restoredSolution.Product.ContentIdentitySha256 != evidence.SolutionIdentitySha256)
                throw new InvalidDataException("Retained solution changed.");
            var solutionArtifact = CreateProcessingArtifact(solutionOutput, restoredSolution);
            var products = new List<ProcessingProduct>();
            var layerInputs = new List<PresentationLayerProductInput>();
            foreach (var residuals in new[] { false, true })
            {
                var kind = residuals ? AstrometricDiagnosticPresentation.ResidualsLayer : AstrometricDiagnosticPresentation.AssociationsLayer;
                var payload = AstrometricDiagnosticPresentation.CreatePayload(evidence.SolutionIdentitySha256!, diagnostics, descriptor.Layout.Width, descriptor.Layout.Height, residuals);
                var product = PresentationProcessingProducts.CreateLayerProduct(payload, kind, [solutionArtifact], AstrometricDiagnosticPresentation.Version);
                products.Add(product);
                var artifact = ToArtifact(product);
                var layer = LayeredPresentationJson.CreateLayer(kind,
                    new(artifact.ArtifactId, payload.ContentIdentitySha256, artifact.MediaType, manifest.BaseProduct.Compatibility),
                    manifest.SceneIdentitySha256, PresentationCoordinateSpace.ScenePixels,
                    PresentationLayerCompositor.AlgorithmVersion, AstrometricDiagnosticPresentation.Version,
                    residuals ? 610 : 600, PresentationBlendMode.Normal, 1_000_000, false,
                    JsonSerializer.SerializeToElement(new
                    {
                        evidence.SolutionIdentitySha256,
                        diagnostics.IdentitySha256,
                        Basis = "MeasuredFit",
                        ResidualScale = 1,
                        UncertaintyBand = "Withheld"
                    }));
                layerInputs.Add(new(layer, artifact));
            }
            var expectedManifest = LayeredPresentationJson.CreateManifest(manifest.BaseProduct, manifest.SceneIdentitySha256,
                manifest.Layers.Concat(layerInputs.Select(input => input.Layer)));
            var expectedRecipe = PresentationProcessingProducts.CreateManifestRecipeIdentity(expectedManifest);
            var expectedSources = new[] { expectedManifest.BaseProduct.ArtifactId }.Concat(expectedManifest.Layers.Select(l => l.SourceProduct.ArtifactId)).ToArray();
            var expectedOutputIdentity = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata,
                AstrometricDiagnosticPresentation.CompositionSchema, expectedRecipe.IdentitySha256, expectedSources);
            var expectedId = ProcessingIdentity.CreateArtifactId(expectedOutputIdentity);
            if (await _store.ReadOutputByArtifactIdAsync(expectedId, cancellationToken).ConfigureAwait(false) is { } existingComposition)
            {
                var restored = await RestoreOutputAsync(existingComposition, cancellationToken).ConfigureAwait(false);
                if (existingComposition.Capture.CaptureId != captureId || restored.Product.SchemaVersion != AstrometricDiagnosticPresentation.CompositionSchema ||
                    restored.Product.ContentIdentitySha256 != expectedManifest.ManifestIdentitySha256 ||
                    restored.Product.ChecksumSha256 != ProcessingIdentity.ComputePayloadSha256(LayeredPresentationJson.Serialize(expectedManifest)))
                    throw new InvalidDataException("Retained diagnostic composition conflicts with its source.");
                foreach (var product in products)
                {
                    var retained = await RequireOutputAsync(ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), captureId, cancellationToken).ConfigureAwait(false);
                    var restoredLayer = await RestoreOutputAsync(retained, cancellationToken).ConfigureAwait(false);
                    if (restoredLayer.Product.SchemaVersion != product.SchemaVersion || restoredLayer.Product.Kind != product.Kind ||
                        restoredLayer.Product.ContentIdentitySha256 != product.ContentIdentitySha256 || restoredLayer.Product.ChecksumSha256 != product.ChecksumSha256 ||
                        !restoredLayer.Product.SourceArtifactIds.SequenceEqual(product.SourceArtifactIds))
                        throw new InvalidDataException("Retained diagnostic layer conflicts with its source.");
                }
                return (expectedId, evidence with { CompatibleBaseArtifactId = baseOutput.ArtifactId, DiagnosticsReason = "Compatible retained measured diagnostics." });
            }
            foreach (var layer in manifest.Layers)
            {
                var output = await RequireOutputAsync(layer.SourceProduct.ArtifactId, captureId, cancellationToken).ConfigureAwait(false);
                var restored = await RestoreOutputAsync(output, cancellationToken).ConfigureAwait(false);
                layerInputs.Add(new(layer, CreateProcessingArtifact(output, restored)));
            }
            var restoredBase = await RestoreOutputAsync(baseOutput, cancellationToken).ConfigureAwait(false);
            if (restoredBase.Artifact is null) throw new InvalidDataException("Presentation base unavailable.");
            var composed = PresentationProcessingProducts.CreateManifestProduct(manifest.BaseProduct, manifest.SceneIdentitySha256,
                layerInputs, AstrometricDiagnosticPresentation.CompositionSchema, CreateProcessingArtifact(baseOutput, restoredBase))
                with
            { SchemaVersion = AstrometricDiagnosticPresentation.CompositionSchema };
            products.Add(composed);
            var composedId = ProcessingIdentity.CreateArtifactId(composed.OutputIdentitySha256);
            if (composedId != expectedId) throw new InvalidDataException("Diagnostic composition identity mismatch.");
            var nodeId = "capture-astrometry-" + composed.OutputIdentitySha256[..16];
            // Validate retained publications, including their layers, on every read. Never trust a cache after source loss.
            var outputs = new List<DurableProcessingOutput>();
            foreach (var product in products)
            {
                var id = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256);
                if (await _store.ReadOutputByArtifactIdAsync(id, cancellationToken).ConfigureAwait(false) is { } retained)
                {
                    var restored = await RestoreOutputAsync(retained, cancellationToken).ConfigureAwait(false);
                    if (retained.Capture.CaptureId != captureId || restored.Product.SchemaVersion != product.SchemaVersion || restored.Product.Kind != product.Kind ||
                        restored.Product.ChecksumSha256 != product.ChecksumSha256 ||
                        restored.Product.ContentIdentitySha256 != product.ContentIdentitySha256 || !restored.Product.SourceArtifactIds.SequenceEqual(product.SourceArtifactIds))
                        throw new InvalidDataException("Retained diagnostic publication conflicts with its source.");
                    // An unchanged layer keeps its original producer. The new manifest references it by artifact ID;
                    // registering it again under this revision's node would violate the durable producer binding.
                }
                else outputs.Add(await PersistMetadataProductAsync(_storageRoot, descriptor, nodeId, product,
                    BeforeMetadataDirectorySyncForTest, provisionStorageRoot: false, cancellationToken).ConfigureAwait(false));
            }
            var now = _timeProvider.GetUtcNow();
            var node = new CaptureProcessingGraphNode(nodeId, OnDemandMaterializationStep.Instance, [], false,
                "capture-astrometric-composition", FrameArtifactRole.Metadata, AstrometricDiagnosticPresentation.CompositionSchema, composed.OutputIdentitySha256);
            var originalArtifact = CreateProcessingArtifact(originalOutput, original);
            var inputs = new[] { originalArtifact, solutionArtifact }.Select((a, index) => new DurableProcessingNodeInput(index,
                "Artifact", index == 0 ? "original-manifest" : "solution", a.ArtifactId, a.Role, a.Variant,
                a.RecipeIdentitySha256, a.SchemaVersion, a.ContentIdentitySha256, true)).ToArray();
            await _store.WriteNodeAsync(captureId, node, DurableProcessingNodeStatus.Completed, null, 1, null, now, now,
                TimeSpan.Zero, ProcessingOutcomeStatus.Produced, inputs, 0, null, outputs, null,
                ProcessingNodeExecutionRoute.Unknown, cancellationToken).ConfigureAwait(false);
            return (composedId, evidence with { CompatibleBaseArtifactId = baseOutput.ArtifactId, DiagnosticsReason = "Compatible retained measured diagnostics." });

            ProcessingArtifact ToArtifact(ProcessingProduct p) => new(ProcessingIdentity.CreateArtifactId(p.OutputIdentitySha256),
                p.Role, p.Variant, p.Recipe.IdentitySha256, p.MediaType, p.Layout, p.Payload, source.Artifact.CreatedUtc,
                p.TotalIntegration, p.Compatibility, source.Capture.CaptureSequence, p.SourceArtifactIds)
            { CaptureId = captureId, ProductKind = p.Kind, SchemaVersion = p.SchemaVersion, ContentIdentitySha256 = p.ContentIdentitySha256 };
        }
        finally { gate.Release(); }
    }

    // The trail helper permits any matching ancestor. A single-capture fit needs the entire science
    // lineage to represent that same exposure, including integration: derivative timing copies alone
    // cannot distinguish a rolling stack from its latest raw. Keep this stricter contract local.
    internal static async ValueTask<bool> HasSingleExposureAstrometricMappingAsync(DurableProcessingOutput output,
        ReconstructionDescriptor source, Func<Guid, CancellationToken, ValueTask<DurableProcessingOutput?>> readOutput,
        CancellationToken cancellationToken)
    {
        if (source.Artifact.Role != FrameArtifactRole.Raw || source.Controls.EffectiveExposure <= TimeSpan.Zero)
            return false;
        var expected = CameraAgentRecipeExecutionAdapter.CreateCompatibility(source);
        var examined = new HashSet<Guid>();
        for (var remainingHops = MaximumLineageHops; ; remainingHops--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!examined.Add(output.ArtifactId) || output.AvailabilityState != "Available" ||
                output.Descriptor is not { } descriptor || descriptor.Artifact.ArtifactId != output.ArtifactId ||
                descriptor.Capture != source.Capture || output.CaptureSequence != source.Capture.CaptureSequence ||
                descriptor.Layout.Width != source.Layout.Width || descriptor.Layout.Height != source.Layout.Height ||
                descriptor.Timing.ExposureStartedUtc != source.Timing.ExposureStartedUtc ||
                descriptor.Timing.ExposureEndedUtc != source.Timing.ExposureEndedUtc ||
                descriptor.Controls.EffectiveExposure != source.Controls.EffectiveExposure ||
                output.TotalIntegration != source.Controls.EffectiveExposure ||
                output.Compatibility.Rig != expected.Rig || output.Compatibility.Orientation != expected.Orientation ||
                output.Compatibility.Sensor != expected.Sensor || output.Compatibility.LocationIdentitySha256 != expected.LocationIdentitySha256)
                return false;
            if (output.ArtifactId == source.Artifact.ArtifactId)
                return CaptureContractJson.ComputeDescriptorSha256(descriptor) == CaptureContractJson.ComputeDescriptorSha256(source);
            if (remainingHops < 1 || !TransientTrailPresentation.PreservesSourcePixels(output.Artifact.Role, output.Artifact.Recipe, output.Algorithms))
                return false;
            var ids = descriptor.Artifact.SourceArtifactIds;
            if (ids.Count is < 1 or > 100 || ids.Distinct().Count() != ids.Count)
                return false;
            // Reference calibration's first canonical input is science; its remaining inputs are masters.
            // Every other recognized producer must have one unambiguous science input, including RollingMean.
            if (output.Artifact.Recipe.Name != BuiltInProcessingRecipes.ReferenceCalibration && ids.Count != 1)
                return false;
            var scienceId = ids[0];
            // The retained reader already authenticated this raw descriptor and payload. Raw ingress
            // need not have a derivative-store row; this final edge still consumes the hop budget.
            if (scienceId == source.Artifact.ArtifactId) return true;
            var parent = await readOutput(scienceId, cancellationToken).ConfigureAwait(false);
            if (parent is null || parent.ArtifactId != scienceId) return false;
            output = parent;
        }
    }

}
