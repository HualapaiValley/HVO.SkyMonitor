using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class CaptureProcessingPersistence
{
    /// <summary>Uses prepared immutable products to freeze outcomes across a failed node commit.</summary>
    internal async ValueTask<(ProcessingOutcome Outcome, bool Restored)> ExecuteRetainedAstrometryAsync(
        CaptureProcessingContext context, ProcessingExecutionRequest request, ProcessingProduct contextProduct,
        Func<ValueTask<ProcessingOutcome>> execute, CancellationToken cancellationToken)
    {
        var descriptor = context.ReconstructionDescriptor ?? throw new InvalidDataException("Missing admitted raw descriptor.");
        var node = context.CurrentNodeId ?? throw new InvalidDataException("Missing durable astrometry node.");
        var identity = RetainedAstrometryProducts.RecipeIdentity(request);
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, request.OutputVariant,
            identity.IdentitySha256, request.Inputs.Select(a => a.ArtifactId).ToArray());
        var artifactId = ProcessingIdentity.CreateArtifactId(outputIdentity);
        var lifecycle = StorageLifecycleLock.ForRoot(_storageRoot);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (context.ProcessingExecution is { } execution)
                await _store.EnsureExecutionLeaseAsync(execution, cancellationToken).ConfigureAwait(false);
            await PublishPreparedAstrometryAsync(descriptor, node, contextProduct, cancellationToken).ConfigureAwait(false);
            await Silo.ResumeProcessingAsync(SiloOutputGroupId(descriptor, node, null, [artifactId]), cancellationToken)
                .ConfigureAwait(false);
            var bytes = await Silo.ReadMetadataAsync(artifactId, outputIdentity, MeasuredAstrometryJson.MaximumSolutionBytes,
                null, cancellationToken).ConfigureAwait(false);
            if (bytes is not null)
            {
                var restored = RetainedAstrometryProducts.Restore(request, bytes, cancellationToken);
                await PublishPreparedAstrometryAsync(descriptor, node, restored, cancellationToken).ConfigureAwait(false);
                return (ProcessingOutcome.Produced(restored), true);
            }
        }
        finally { lifecycle.Release(); }
        // Numerical work never holds the storage lifecycle lock.
        var outcome = await execute().ConfigureAwait(false);
        if (outcome.Status != ProcessingOutcomeStatus.Produced) return (outcome, false);
        var product = outcome.Products.Single();
        _ = RetainedAstrometryProducts.Verify(request, product, cancellationToken);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (context.ProcessingExecution is { } execution)
                await _store.EnsureExecutionLeaseAsync(execution, cancellationToken).ConfigureAwait(false);
            await PublishPreparedAstrometryAsync(descriptor, node, product, cancellationToken).ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
        return (outcome, false);
    }

    private async ValueTask PublishPreparedAstrometryAsync(ReconstructionDescriptor descriptor, string node, ProcessingProduct product,
        CancellationToken cancellationToken)
    {
        var output = DescribeMetadataProduct(descriptor, node, product);
        await PersistSiloOutputsAsync(descriptor, [output], [product], node, null, cancellationToken).ConfigureAwait(false);
    }
}
