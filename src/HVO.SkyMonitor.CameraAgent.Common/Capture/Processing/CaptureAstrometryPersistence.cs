using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
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
        var utc = descriptor.Timing.ReadoutCompletedUtc.ToUniversalTime();
        var path = Path.Combine(_storageRoot, "derived", utc.Year.ToString("D4", CultureInfo.InvariantCulture),
            utc.Month.ToString("D2", CultureInfo.InvariantCulture), utc.Day.ToString("D2", CultureInfo.InvariantCulture),
            FrameArtifactRole.Metadata.ToString(), string.Concat(utc.ToString("yyyy-MM-dd_HH-mm-ss.fff'Z'", CultureInfo.InvariantCulture),
                "-", artifactId.ToString("N"), ".json"));
        var lifecycle = StorageLifecycleLock.ForRoot(_storageRoot);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (context.ProcessingExecution is { } execution)
                await _store.EnsureExecutionLeaseAsync(execution, cancellationToken).ConfigureAwait(false);
            await PublishPreparedAstrometryAsync(descriptor, node, contextProduct, cancellationToken).ConfigureAwait(false);
            byte[]? bytes;
            if (Silo is { } siloStorage)
            {
                await siloStorage.ResumeProcessingAsync(SiloOutputGroupId(descriptor, node, null, [artifactId]), cancellationToken)
                    .ConfigureAwait(false);
                bytes = await siloStorage.ReadMetadataAsync(artifactId, outputIdentity, MeasuredAstrometryJson.MaximumSolutionBytes,
                    null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                RawIngressFileStore.EnsureNoSymbolicLinks(_storageRoot, path);
                bytes = null;
                if (File.Exists(path))
                {
                    var length = new FileInfo(path).Length;
                    if (length is <= 0 or > MeasuredAstrometryJson.MaximumSolutionBytes)
                        throw new InvalidDataException("Prepared astrometry exceeds its byte bound.");
                    bytes = await ReadCommittedFileAsync(path, length, cancellationToken).ConfigureAwait(false);
                }
            }
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
        if (Silo is null)
        {
            await CopyMetadataProductAsync(_storageRoot, descriptor, node, product, cancellationToken).ConfigureAwait(false);
            return;
        }

        var output = await PersistMetadataProductAsync(_storageRoot, descriptor, node, product, null,
            provisionStorageRoot: false, cancellationToken, writeLocalCopies: false).ConfigureAwait(false);
        await PersistSiloOutputsAsync(descriptor, [output], [product], node, null, cancellationToken).ConfigureAwait(false);
    }
}
