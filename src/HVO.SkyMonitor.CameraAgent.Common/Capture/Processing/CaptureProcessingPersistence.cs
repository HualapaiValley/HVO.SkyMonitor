using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using System.Diagnostics;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CaptureProcessingPersistence(
    IOptions<CameraAgentHostOptions> options,
    SqliteCaptureProcessingStore store,
    IFrameStorageService frameStorage,
    CaptureProcessingTelemetry telemetry,
    ILogger<CaptureProcessingPersistence> logger) : IProcessingRetentionHolds
{
    private readonly string _storageRoot = Path.GetFullPath(options.Value.RawIngressRoot);
    private readonly SqliteCaptureProcessingStore _store = store;
    private readonly IFrameStorageService _frameStorage = frameStorage;
    private readonly CaptureProcessingTelemetry _telemetry = telemetry;
    private readonly ILogger<CaptureProcessingPersistence> _logger = logger;

    internal ValueTask InitializeAsync(CancellationToken cancellationToken)
        => _store.InitializeAsync(cancellationToken);

    internal ValueTask<DurableProcessingNode?> ReadNodeAsync(
        Guid captureId,
        string nodeId,
        CancellationToken cancellationToken)
        => _store.ReadNodeAsync(captureId, nodeId, cancellationToken);

    public ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsAsync(
        string storageRoot,
        CancellationToken cancellationToken)
        => PathsEqual(storageRoot, _storageRoot)
            ? _store.ReadRetentionHoldsAsync(cancellationToken)
            : ValueTask.FromResult<IReadOnlyList<ProcessingRetentionHold>>([]);

    internal async ValueTask RestoreNodeAsync(
        DurableProcessingNode durableNode,
        CaptureProcessingContext context,
        CancellationToken cancellationToken)
    {
        using var activity = CaptureProcessingTelemetry.ActivitySource.StartActivity("processing-graph.recover");
        foreach (var output in durableNode.Outputs)
        {
            var restored = await RestoreOutputAsync(output, cancellationToken).ConfigureAwait(false);
            context.RestoreProduct(durableNode.NodeId, restored.Artifact, restored.Product);
            _telemetry.RecordRecovered(restored.Product.Role, restored.Product.Variant);
        }
        _logger.CaptureProcessingNodeRecovered(durableNode.NodeId, durableNode.Outputs.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    internal async ValueTask<IReadOnlyList<ProcessingArtifact>> ReadRecentInputsAsync(
        ReconstructionDescriptor currentDescriptor,
        string nodeId,
        FrameArtifactRole role,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        var outputs = await _store.ReadRecentOutputsAsync(
            currentDescriptor.Capture.AgentId,
            currentDescriptor.Capture.CaptureSequence,
            nodeId, role, maximumCount, cancellationToken).ConfigureAwait(false);
        var artifacts = new List<ProcessingArtifact>(outputs.Count);
        foreach (var output in outputs)
        {
            var restored = await RestoreOutputAsync(output, cancellationToken).ConfigureAwait(false);
            artifacts.Add(new ProcessingArtifact(
                restored.Artifact.ArtifactId,
                restored.Product.Role,
                restored.Product.Variant,
                restored.Product.Recipe.IdentitySha256,
                restored.Product.MediaType,
                restored.Product.Layout,
                restored.Product.Payload,
                restored.Artifact.Frame.TimestampUtc,
                restored.Product.TotalIntegration,
                restored.Product.Compatibility));
        }
        return artifacts;
    }

    internal async ValueTask<IReadOnlyList<ProcessingArtifact>> ReadRecentRawInputsAsync(
        ReconstructionDescriptor currentDescriptor,
        ProcessingArtifact current,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        var entries = await _store.ReadRecentRawInputsAsync(
            currentDescriptor.Capture.AgentId,
            currentDescriptor.Capture.CaptureSequence,
            maximumCount,
            cancellationToken).ConfigureAwait(false);
        var artifacts = new List<ProcessingArtifact>(maximumCount);
        foreach (var entry in entries)
        {
            if (!RawIsCompatible(currentDescriptor, entry.Descriptor))
            {
                break;
            }
            var payloadPath = ResolveSafePath(entry.PayloadRelativePath);
            if (!File.Exists(payloadPath))
            {
                break;
            }
            var payload = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                ProcessingIdentity.ComputePayloadSha256(payload),
                entry.Descriptor.Artifact.ChecksumSha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Durable raw rolling input checksum is invalid.");
            }
            artifacts.Add(new ProcessingArtifact(
                entry.Descriptor.Artifact.ArtifactId,
                FrameArtifactRole.Raw,
                entry.Descriptor.Artifact.Variant,
                ProcessingIdentity.CreateRecipeIdentity(entry.Descriptor.Artifact.Recipe).IdentitySha256,
                entry.Descriptor.Artifact.MediaType,
                entry.Descriptor.Layout,
                payload,
                entry.Descriptor.Artifact.CreatedUtc,
                entry.Descriptor.Controls.EffectiveExposure,
                current.Compatibility));
        }
        artifacts.Reverse();
        return artifacts;
    }

    internal async ValueTask WriteNodeAsync(
        RawCaptureReceipt rawCapture,
        CaptureProcessingGraphNode node,
        DurableProcessingNodeStatus status,
        string? reason,
        int attempt,
        long workId,
        string? leaseToken,
        IReadOnlyList<ProcessingProduct> products,
        CaptureProcessingContext context,
        CancellationToken cancellationToken)
    {
        var lifecycleGate = StorageLifecycleLock.ForRoot(_storageRoot);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outputs = new List<DurableProcessingOutput>(products.Count);
            foreach (var product in products)
            {
                var stopwatch = Stopwatch.StartNew();
                using var activity = CaptureProcessingTelemetry.ActivitySource.StartActivity("processing-artifact.persist");
                var artifact = context.FindArtifact(product);
                var descriptor = DerivativeDescriptorFactory.Create(
                    rawCapture.Manifest.Descriptor,
                    artifact.ArtifactId,
                    artifact.Frame.Metadata.SourceId ?? node.Id,
                    product);
                var stored = await _frameStorage.SaveAsync(
                    _storageRoot, artifact, descriptor, cancellationToken).ConfigureAwait(false);
                outputs.Add(new DurableProcessingOutput(
                    product.OutputIdentitySha256,
                    artifact.ArtifactId,
                    NormalizeRelativePath(stored.RelativePath),
                    NormalizeRelativePath(Path.ChangeExtension(stored.RelativePath, ".json")),
                    descriptor,
                    product.Recipe.IdentitySha256,
                    product.Algorithms,
                    product.Compatibility,
                    product.TotalIntegration,
                    rawCapture.Manifest.Descriptor.Capture.CaptureSequence,
                    artifact.RecipeVersion));
                stopwatch.Stop();
                _telemetry.RecordPersistence(node, product, stopwatch.Elapsed);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            await _store.WriteNodeAsync(
                rawCapture.Manifest.Descriptor.Capture.CaptureId,
                node,
                status,
                reason,
                attempt,
                workId,
                leaseToken,
                outputs,
                cancellationToken).ConfigureAwait(false);
            if (outputs.Count > 0)
            {
                _logger.CaptureProcessingOutputPersisted(
                    node.Id,
                    outputs.Count,
                    products.Sum(static product => (long)product.Payload.Length));
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async ValueTask<(FrameArtifact Artifact, ProcessingProduct Product)> RestoreOutputAsync(
        DurableProcessingOutput output,
        CancellationToken cancellationToken)
    {
        var payloadPath = ResolveSafePath(output.PayloadRelativePath);
        var sidecarPath = ResolveSafePath(output.SidecarRelativePath);
        var sidecar = await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
        var parsed = CaptureContractJson.ParseManifest(sidecar);
        if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest ||
            !string.Equals(manifest.IdempotencyKey,
                CaptureContractJson.ComputeDescriptorSha256(output.Descriptor),
                StringComparison.Ordinal) ||
            !string.Equals(
                NormalizeRelativePath(manifest.RelativeArtifactPath),
                NormalizeRelativePath(output.PayloadRelativePath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Committed processing output sidecar conflicts with durable state.");
        }
        var payload = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        var reconstruction = FrameReconstructor.TryReconstruct(output.Descriptor, payload, out var frame);
        if (!reconstruction.IsValid || frame is null)
        {
            throw new InvalidDataException($"Committed processing output is not reconstructable ({reconstruction.ReasonCode}).");
        }
        var recipe = ProcessingIdentity.CreateRecipeIdentity(output.Descriptor.Artifact.Recipe);
        if (!string.Equals(recipe.IdentitySha256, output.RecipeIdentitySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Committed processing recipe identity conflicts with its descriptor.");
        }
        var expectedOutputIdentity = ProcessingIdentity.CreateOutputIdentity(
            output.Descriptor.Artifact.Role,
            output.Descriptor.Artifact.Variant,
            recipe.IdentitySha256,
            output.Descriptor.Artifact.SourceArtifactIds);
        if (!string.Equals(expectedOutputIdentity, output.OutputIdentitySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Committed processing output identity conflicts with its lineage.");
        }
        var product = new ProcessingProduct(
            output.Descriptor.Artifact.Role,
            output.Descriptor.Artifact.Variant,
            output.OutputIdentitySha256,
            output.Descriptor.Artifact.MediaType,
            output.Descriptor.Layout,
            payload,
            output.Descriptor.Artifact.ChecksumSha256,
            recipe,
            output.Algorithms,
            output.Descriptor.Artifact.SourceArtifactIds,
            output.TotalIntegration,
            output.Compatibility);
        var artifact = new FrameArtifact(
            output.ArtifactId,
            product.Role,
            frame,
            product.SourceArtifactIds,
            output.LegacyRecipeVersion ?? product.Recipe.Descriptor.ImplementationVersion);
        return (artifact, product);
    }

    private string ResolveSafePath(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(
            _storageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = string.Concat(_storageRoot.TrimEnd(Path.DirectorySeparatorChar), Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidDataException("Committed processing output path escapes the storage root.");
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(_storageRoot, fullPath);
        return fullPath;
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/');

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool RawIsCompatible(ReconstructionDescriptor current, ReconstructionDescriptor candidate)
        => current.Layout == candidate.Layout &&
           current.Profiles.Rig == candidate.Profiles.Rig &&
           current.Profiles.Calibration == candidate.Profiles.Calibration &&
           current.Profiles.Mask == candidate.Profiles.Mask &&
           current.Profiles.Sensor == candidate.Profiles.Sensor &&
           current.Profiles.Processing == candidate.Profiles.Processing &&
           current.Controls.EffectiveExposure == candidate.Controls.EffectiveExposure &&
           current.Controls.EffectiveGain == candidate.Controls.EffectiveGain &&
           current.Controls.EffectiveOffset == candidate.Controls.EffectiveOffset &&
           current.Controls.TemperatureSetpointC == candidate.Controls.TemperatureSetpointC;
}
