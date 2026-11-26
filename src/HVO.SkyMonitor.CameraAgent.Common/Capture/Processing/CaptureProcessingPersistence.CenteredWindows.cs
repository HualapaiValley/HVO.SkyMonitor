using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class CaptureProcessingPersistence
{
    internal async ValueTask<CenteredWindowDisposition> PersistCenteredRegistrationAsync(
        CenteredWindowRegistrationResult result,
        ReconstructionDescriptor reference,
        ICenteredWindowJournal journal,
        CancellationToken cancellationToken)
    {
        if (result.RecomputationId is not null)
            throw new InvalidOperationException("Archive recomputation cannot publish a live centered window.");
        var source = result.Closure.Center.Input!;
        if (reference.Capture.CaptureId != source.Key.CaptureId ||
            CaptureContractJson.ComputeDescriptorSha256(reference) != source.RawDescriptorSha256)
            throw new InvalidDataException("The registration writer could not authenticate the original reference.");

        var gate = StorageLifecycleLock.ForRoot(_storageRoot);
        await StorageLifecycleLock.WaitAsync(gate, "centered-registration", _logger, cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _store.ReadCenteredWindowDispositionAsync(result.Closure.Schedule.Identity, cancellationToken).ConfigureAwait(false);
            if (existing is { IsTerminal: true })
            {
                if (existing.Publication is { } prior &&
                    !prior.RegistrationArtifactIds.SequenceEqual(result.Publication.RegistrationArtifactIds))
                    throw new InvalidDataException("The already published registration has different artifacts.");
                return existing;
            }

            var outputs = new List<DurableProcessingOutput>(result.Products.Length);
            foreach (var product in result.Products)
            {
                ValidateProductForPublication(product);
                if (product.Layout is not { } layout)
                {
                    outputs.Add(await PersistMetadataProductAsync(_storageRoot, reference, CenteredWindowCoordinator.RegistrationProducer,
                        product, BeforeMetadataDirectorySyncForTest, provisionStorageRoot: false, cancellationToken).ConfigureAwait(false));
                    continue;
                }

                const string producer = CenteredWindowCoordinator.RegistrationProducer;
                var frame = new CameraFrame(reference.Timing.ExposureStartedUtc, layout.Width, layout.Height, layout.PixelFormat,
                    product.Payload, new(reference.Controls.EffectiveExposure, reference.Controls.EffectiveGain,
                        reference.Controls.EffectiveTemperatureC ?? double.NaN, producer, Offset: reference.Controls.EffectiveOffset),
                    layout.StrideBytes)
                { Layout = layout };
                var artifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256);
                var artifact = new FrameArtifact(artifactId, product.Role, frame, product.SourceArtifactIds,
                    product.Recipe.Descriptor.ImplementationVersion);
                var descriptor = DerivativeDescriptorFactory.Create(reference, artifactId, producer, product);
                var stored = await _frameStorage.SaveAsync(_storageRoot, artifact, descriptor, producer, cancellationToken).ConfigureAwait(false);
                var path = NormalizeRelativePath(stored.RelativePath);
                var manifest = new ArtifactManifestV2(ArtifactManifestV2.CurrentSchemaVersion, descriptor, path, null, producer);
                outputs.Add(new(product.OutputIdentitySha256, artifactId, path, NormalizeRelativePath(Path.ChangeExtension(path, ".json")),
                    CaptureContractJson.Serialize(manifest), descriptor, null, product.Recipe.IdentitySha256, product.Algorithms,
                    product.Compatibility, product.TotalIntegration, reference.Capture.CaptureSequence,
                    FrameArtifactRecipeVersion: artifact.RecipeVersion));
            }
            await PersistSiloOutputsAsync(reference, outputs, result.Products, CenteredWindowCoordinator.RegistrationProducer,
                null, cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit, CenteredWindowCoordinator.RegistrationProducer);
            return await _store.PublishCenteredWindowAsync(result, outputs, journal, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async ValueTask<ProcessingArtifact?> ReadCenteredRetainedProductAsync(
        ExposureKey exposure, CenteredStackArtifactPin pin, CancellationToken cancellationToken)
    {
        var output = await FindCenteredRetainedOutputAsync(exposure, pin.ArtifactId, cancellationToken).ConfigureAwait(false);
        if (output is null)
            return null;
        var header = CenteredWindowArtifactHeader.FromOutput(output);
        if (CenteredStackArtifactPin.FromHeader(CenteredStackArtifactHeader.FromArtifact(header), output.Artifact.ChecksumSha256) != pin)
            throw new InvalidDataException("The retained registration header differs from its frozen identity.");
        var restored = await RestoreOutputAsync(output, cancellationToken).ConfigureAwait(false);
        var artifact = header with { Payload = restored.Product.Payload };
        if (CenteredStackArtifactPin.From(artifact) != pin)
            throw new InvalidDataException("The retained registration product differs from its frozen identity.");
        return artifact;
    }

    private async ValueTask<DurableProcessingOutput?> FindCenteredRetainedOutputAsync(
        ExposureKey exposure, Guid artifactId, CancellationToken cancellationToken)
    {
        var output = await _store.ReadOutputByArtifactIdAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (output is null || output.AvailabilityState != "Available")
            return null;
        if (output.Capture.CaptureId != exposure.CaptureId || output.Capture.AgentId != exposure.Camera.AgentId)
            throw new InvalidDataException("The retained centered product belongs to a different capture.");
        return output;
    }
}
