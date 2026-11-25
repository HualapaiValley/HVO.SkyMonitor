using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class CaptureProcessingPersistence
{
    private const string CenteredViewPublicationProducer = "centered-view-publication";
    private const string CenteredViewPreviewNode = CenteredViewPublicationProducer + "-preview";

    internal async ValueTask<ProcessingArtifact> ReadCenteredViewProductAsync(Guid captureId, CenteredViewArtifact pin,
        CancellationToken cancellationToken)
    {
        var output = await RequireCenteredViewOutputAsync(captureId, pin, cancellationToken).ConfigureAwait(false);
        return CreateProcessingArtifact(output, await RestoreOutputAsync(output, cancellationToken).ConfigureAwait(false));
    }

    internal async ValueTask<ProcessingProduct> ReadCenteredViewRetainedProductAsync(Guid captureId, CenteredViewArtifact pin,
        CancellationToken cancellationToken)
    {
        var output = await RequireCenteredViewOutputAsync(captureId, pin, cancellationToken).ConfigureAwait(false);
        return (await RestoreOutputAsync(output, cancellationToken).ConfigureAwait(false)).Product;
    }

    internal async ValueTask<ProcessingArtifact> ReadCenteredViewParentAsync(Guid captureId, Guid artifactId,
        CancellationToken token)
    {
        var output = await RequireOutputAsync(artifactId, captureId, token).ConfigureAwait(false);
        return CreateProcessingArtifact(output, await RestoreOutputAsync(output, token).ConfigureAwait(false));
    }

    internal async ValueTask<(ProcessingArtifact Artifact, ExposureMeasurementV1 Measurement)> ReadCenteredViewMeasurementAsync(
        Guid captureId, CenteredViewArtifact pin, CancellationToken cancellationToken)
    {
        var output = await RequireCenteredViewOutputAsync(captureId, pin, cancellationToken).ConfigureAwait(false);
        var restored = await RestoreOutputAsync(output, cancellationToken).ConfigureAwait(false);
        return (CreateProcessingArtifact(output, restored), ExposureMeasurementProducts.Read(restored.Product));
    }

    private async ValueTask<DurableProcessingOutput> RequireCenteredViewOutputAsync(Guid captureId, CenteredViewArtifact pin,
        CancellationToken cancellationToken)
    {
        var output = await RequireOutputAsync(pin.ArtifactId, captureId, cancellationToken).ConfigureAwait(false);
        if (output.OutputIdentitySha256 != pin.IdentitySha256 || output.Artifact.ChecksumSha256 != pin.PayloadSha256)
            throw new InvalidDataException("The retained centered product does not match its execution binding.");
        return output;
    }

    internal async ValueTask PersistCenteredViewProductAsync(
        ReconstructionDescriptor reference, ProcessingProduct product, CancellationToken cancellationToken)
    {
        ValidateProductForPublication(product);
        var gate = StorageLifecycleLock.ForRoot(_storageRoot);
        await StorageLifecycleLock.WaitAsync(gate, CenteredViewPublicationProducer, _logger, cancellationToken).ConfigureAwait(false);
        try
        {
            // A stable producer node preserves the journal's bounded per-producer retention ranking.
            var nodeId = product.Role == FrameArtifactRole.Preview ? CenteredViewPreviewNode : CenteredViewPublicationProducer + "-layer";
            var output = await PersistMetadataProductAsync(_storageRoot, reference, nodeId, product,
                BeforeMetadataDirectorySyncForTest, provisionStorageRoot: false, cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit, CenteredViewPublicationProducer);
            var node = new CaptureProcessingGraphNode(nodeId, OnDemandMaterializationStep.Instance, [], false,
                product.Recipe.Descriptor.Name, product.Role, product.Variant, product.Recipe.IdentitySha256);
            await _store.WriteNodeAsync(reference.Capture.CaptureId, node, DurableProcessingNodeStatus.Completed, null, 1,
                reference.Profiles.Processing.Sha256, null, _timeProvider.GetUtcNow(), null,
                ProcessingOutcomeStatus.Produced, [], 0, null, [output], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
