using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CaptureProcessingPersistence(
    IOptions<CameraAgentHostOptions> options,
    SqliteCaptureProcessingStore store,
    IFrameStorageService frameStorage,
    CaptureProcessingTelemetry telemetry,
    ILogger<CaptureProcessingPersistence> logger,
    ICaptureProcessingFaultInjector? faultInjector = null) : IProcessingRetentionHolds
{
    private readonly string _storageRoot = Path.GetFullPath(options.Value.RawIngressRoot);
    private readonly SqliteCaptureProcessingStore _store = store;
    private readonly IFrameStorageService _frameStorage = frameStorage;
    private readonly CaptureProcessingTelemetry _telemetry = telemetry;
    private readonly ILogger<CaptureProcessingPersistence> _logger = logger;
    private readonly ICaptureProcessingFaultInjector _faultInjector =
        faultInjector ?? NullCaptureProcessingFaultInjector.Instance;

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
            if (restored.Artifact is not null)
            {
                context.RestoreProduct(durableNode.NodeId, restored.Artifact, restored.Product);
            }
            else
            {
                context.RestoreProduct(durableNode.NodeId, restored.ArtifactId, restored.Product);
            }
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
            var observationStartedUtc = output.Descriptor?.Timing.ExposureStartedUtc;
            DateTimeOffset? observationEndedUtc = observationStartedUtc is { } startedUtc && output.Descriptor is { } descriptor
                ? ProcessingArtifact.ResolveObservationEndedUtc(
                    startedUtc,
                    descriptor.Timing.ExposureEndedUtc,
                    restored.Product.TotalIntegration)
                : null;
            artifacts.Add(new ProcessingArtifact(
                restored.ArtifactId,
                restored.Product.Role,
                restored.Product.Variant,
                restored.Product.Recipe.IdentitySha256,
                restored.Product.MediaType,
                restored.Product.Layout,
                restored.Product.Payload,
                restored.CreatedUtc,
                restored.Product.TotalIntegration,
                restored.Product.Compatibility,
                CaptureSequence: output.CaptureSequence,
                ObservationStartedUtc: observationStartedUtc,
                ObservationEndedUtc: observationEndedUtc));
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
                CameraAgentRecipeExecutionAdapter.CreateCompatibility(entry.Descriptor),
                CaptureSequence: entry.Descriptor.Capture.CaptureSequence,
                ObservationStartedUtc: entry.Descriptor.Timing.ExposureStartedUtc,
                ObservationEndedUtc: ProcessingArtifact.ResolveObservationEndedUtc(
                    entry.Descriptor.Timing.ExposureStartedUtc,
                    entry.Descriptor.Timing.ExposureEndedUtc,
                    entry.Descriptor.Controls.EffectiveExposure)));
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
        DateTimeOffset? startedUtc,
        DateTimeOffset completedUtc,
        TimeSpan? duration,
        ProcessingOutcomeStatus? outcome,
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
                if (product.Layout is null && product.Role == FrameArtifactRole.Metadata)
                {
                    outputs.Add(await PersistMetadataProductAsync(
                        rawCapture.Manifest.Descriptor, node, product, cancellationToken).ConfigureAwait(false));
                }
                else if (product.Layout is null)
                {
                    throw new InvalidDataException(
                        "Only metadata products may use layoutless CameraAgent durable persistence.");
                }
                else
                {
                    var artifact = context.FindArtifact(product);
                    var descriptor = DerivativeDescriptorFactory.Create(
                        rawCapture.Manifest.Descriptor,
                        artifact.ArtifactId,
                        artifact.Frame.Metadata.SourceId ?? node.Id,
                        product);
                    var stored = await _frameStorage.SaveAsync(
                        _storageRoot, artifact, descriptor, cancellationToken).ConfigureAwait(false);
                    var relativePayloadPath = NormalizeRelativePath(stored.RelativePath);
                    var evidenceJson = CaptureContractJson.Serialize(new ArtifactManifestV2(
                        ArtifactManifestV2.CurrentSchemaVersion,
                        descriptor,
                        relativePayloadPath,
                        artifact.Frame.Metadata.Scene));
                    outputs.Add(new DurableProcessingOutput(
                        product.OutputIdentitySha256,
                        artifact.ArtifactId,
                        relativePayloadPath,
                        NormalizeRelativePath(Path.ChangeExtension(stored.RelativePath, ".json")),
                        evidenceJson,
                        descriptor,
                        null,
                        product.Recipe.IdentitySha256,
                        product.Algorithms,
                        product.Compatibility,
                        product.TotalIntegration,
                        rawCapture.Manifest.Descriptor.Capture.CaptureSequence,
                        artifact.RecipeVersion));
                }
                stopwatch.Stop();
                _telemetry.RecordPersistence(node, product, stopwatch.Elapsed);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            _faultInjector.Inject(CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit, node.Id);
            await _store.WriteNodeAsync(
                rawCapture.Manifest.Descriptor.Capture.CaptureId,
                node,
                status,
                reason,
                attempt,
                string.Equals(
                    RawCaptureDescriptorFactory.CreateProcessingProfile(context.Config).Sha256,
                    rawCapture.Manifest.Descriptor.Profiles.Processing.Sha256,
                    StringComparison.Ordinal)
                    ? rawCapture.Manifest.Descriptor.Profiles.Processing.Sha256
                    : null,
                startedUtc,
                completedUtc,
                duration,
                outcome,
                context.GetCurrentInputEvidence(),
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

    private async ValueTask<RestoredProcessingOutput> RestoreOutputAsync(
        DurableProcessingOutput output,
        CancellationToken cancellationToken)
    {
        var payloadPath = ResolveSafePath(output.PayloadRelativePath);
        var sidecarPath = ResolveSafePath(output.SidecarRelativePath);
        var sidecar = await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
        if (output.ProductManifest is not null)
        {
            return await RestoreMetadataProductAsync(
                output, payloadPath, sidecar, cancellationToken).ConfigureAwait(false);
        }

        var descriptor = output.Descriptor
            ?? throw new InvalidDataException("Committed processing output has no reconstruction descriptor.");
        var parsed = CaptureContractJson.ParseManifest(sidecar);
        if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest ||
            !string.Equals(manifest.IdempotencyKey,
                CaptureContractJson.ComputeDescriptorSha256(descriptor),
                StringComparison.Ordinal) ||
            !string.Equals(
                NormalizeRelativePath(manifest.RelativeArtifactPath),
                NormalizeRelativePath(output.PayloadRelativePath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Committed processing output sidecar conflicts with durable state.");
        }
        var payload = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        var reconstruction = FrameReconstructor.TryReconstruct(descriptor, payload, out var frame);
        if (!reconstruction.IsValid || frame is null)
        {
            throw new InvalidDataException($"Committed processing output is not reconstructable ({reconstruction.ReasonCode}).");
        }
        var recipe = ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe);
        if (!string.Equals(recipe.IdentitySha256, output.RecipeIdentitySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Committed processing recipe identity conflicts with its descriptor.");
        }
        var expectedOutputIdentity = ProcessingIdentity.CreateOutputIdentity(
            descriptor.Artifact.Role,
            descriptor.Artifact.Variant,
            recipe.IdentitySha256,
            descriptor.Artifact.SourceArtifactIds);
        if (!string.Equals(expectedOutputIdentity, output.OutputIdentitySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Committed processing output identity conflicts with its lineage.");
        }
        var product = new ProcessingProduct(
            descriptor.Artifact.Role,
            descriptor.Artifact.Variant,
            output.OutputIdentitySha256,
            descriptor.Artifact.MediaType,
            descriptor.Layout,
            payload,
            descriptor.Artifact.ChecksumSha256,
            recipe,
            output.Algorithms,
            descriptor.Artifact.SourceArtifactIds,
            output.TotalIntegration,
            output.Compatibility);
        var artifact = new FrameArtifact(
            output.ArtifactId,
            product.Role,
            frame,
            product.SourceArtifactIds,
            output.LegacyRecipeVersion ?? product.Recipe.Descriptor.ImplementationVersion);
        return new RestoredProcessingOutput(
            artifact.ArtifactId,
            frame.TimestampUtc,
            artifact,
            product);
    }

    private async ValueTask<DurableProcessingOutput> PersistMetadataProductAsync(
        ReconstructionDescriptor sourceDescriptor,
        CaptureProcessingGraphNode node,
        ProcessingProduct product,
        CancellationToken cancellationToken)
        => await PersistMetadataProductAsync(
            _storageRoot,
            sourceDescriptor,
            node.Id,
            product,
            cancellationToken).ConfigureAwait(false);

    internal static async ValueTask CopyMetadataProductAsync(
        string storageRoot,
        ReconstructionDescriptor sourceDescriptor,
        string sourceId,
        ProcessingProduct product,
        CancellationToken cancellationToken)
        => _ = await PersistMetadataProductAsync(
            Path.GetFullPath(storageRoot),
            sourceDescriptor,
            sourceId,
            product,
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask<DurableProcessingOutput> PersistMetadataProductAsync(
        string storageRoot,
        ReconstructionDescriptor sourceDescriptor,
        string sourceId,
        ProcessingProduct product,
        CancellationToken cancellationToken)
    {
        var checksum = ProcessingIdentity.ComputePayloadSha256(product.Payload);
        var expectedOutputIdentity = ProcessingIdentity.CreateOutputIdentity(
            product.Role,
            product.Variant,
            product.Recipe.IdentitySha256,
            product.SourceArtifactIds);
        if (product.Role != FrameArtifactRole.Metadata ||
            !string.Equals(checksum, product.ChecksumSha256, StringComparison.Ordinal) ||
            !string.Equals(expectedOutputIdentity, product.OutputIdentitySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Layoutless processing product has invalid immutable facts.");
        }
        var artifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256);
        try
        {
            using var _ = JsonDocument.Parse(product.Payload);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Layoutless processing product payload is not valid JSON.", exception);
        }

        var createdUtc = sourceDescriptor.Timing.ReadoutCompletedUtc.ToUniversalTime();
        var directory = Path.Combine(
            storageRoot,
            "derived",
            createdUtc.Year.ToString("D4", CultureInfo.InvariantCulture),
            createdUtc.Month.ToString("D2", CultureInfo.InvariantCulture),
            createdUtc.Day.ToString("D2", CultureInfo.InvariantCulture),
            product.Role.ToString());
        var stem = string.Concat(
            createdUtc.ToString("yyyy-MM-dd_HH-mm-ss.fff'Z'", CultureInfo.InvariantCulture),
            "-",
            artifactId.ToString("N"));
        var payloadPath = Path.Combine(directory, string.Concat(stem, ".json"));
        var sidecarPath = Path.Combine(directory, string.Concat(stem, ".manifest.json"));
        var payloadRelativePath = NormalizeRelativePath(Path.GetRelativePath(storageRoot, payloadPath));
        var sidecarRelativePath = NormalizeRelativePath(Path.GetRelativePath(storageRoot, sidecarPath));
        var artifact = new ArtifactDescriptor(
            artifactId,
            product.Role,
            sourceId,
            product.Variant,
            createdUtc,
            product.SourceArtifactIds,
            product.Recipe.Descriptor,
            product.MediaType,
            product.ChecksumSha256);
        using var nullDocument = JsonDocument.Parse("null");
        var manifest = new DurableProcessingProductManifestV1(
            DurableProcessingProductManifestV1.CurrentSchemaVersion,
            sourceDescriptor.Capture,
            artifact,
            product.OutputIdentitySha256,
            product.Algorithms,
            product.Compatibility,
            product.TotalIntegration.Ticks,
            product.Payload.Length,
            payloadRelativePath,
            nullDocument.RootElement.Clone());
        var evidenceJson = DurableProcessingProductManifestJson.Serialize(manifest);

        var directoryExisted = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, directory);
        if (!directoryExisted)
        {
            RawIngressFileStore.SyncDirectoryHierarchy(storageRoot, directory);
        }
        await ValidateExistingMetadataEvidenceAsync(
            payloadPath, sidecarPath, product.Payload, product.ChecksumSha256, evidenceJson, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(payloadPath))
        {
            await WriteAtomicallyAsync(storageRoot, payloadPath, product.Payload, cancellationToken).ConfigureAwait(false);
        }
        if (!File.Exists(sidecarPath))
        {
            await WriteAtomicallyAsync(storageRoot, sidecarPath, evidenceJson, cancellationToken).ConfigureAwait(false);
        }

        return new DurableProcessingOutput(
            product.OutputIdentitySha256,
            artifactId,
            payloadRelativePath,
            sidecarRelativePath,
            evidenceJson,
            null,
            manifest,
            product.Recipe.IdentitySha256,
            product.Algorithms,
            product.Compatibility,
            product.TotalIntegration,
            sourceDescriptor.Capture.CaptureSequence,
            null);
    }

    private static async ValueTask ValidateExistingMetadataEvidenceAsync(
        string payloadPath,
        string sidecarPath,
        ReadOnlyMemory<byte> expectedPayload,
        string expectedChecksum,
        ReadOnlyMemory<byte> expectedSidecar,
        CancellationToken cancellationToken)
    {
        var payloadExists = File.Exists(payloadPath);
        var sidecarExists = File.Exists(sidecarPath);
        if (sidecarExists && !payloadExists)
        {
            throw new InvalidDataException("Existing metadata sidecar has no payload.");
        }
        if (payloadExists)
        {
            var payload = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(ProcessingIdentity.ComputePayloadSha256(payload), expectedChecksum, StringComparison.Ordinal) ||
                !payload.AsSpan().SequenceEqual(expectedPayload.Span))
            {
                throw new InvalidDataException("Existing metadata payload conflicts with the requested output identity.");
            }
        }
        if (sidecarExists)
        {
            var sidecar = await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
            if (!sidecar.AsSpan().SequenceEqual(expectedSidecar.Span))
            {
                throw new InvalidDataException("Existing metadata sidecar conflicts with the requested output identity.");
            }
        }
    }

    private static async ValueTask<RestoredProcessingOutput> RestoreMetadataProductAsync(
        DurableProcessingOutput output,
        string payloadPath,
        byte[] sidecar,
        CancellationToken cancellationToken)
    {
        if (!sidecar.AsSpan().SequenceEqual(output.EvidenceJson))
        {
            throw new InvalidDataException("Committed metadata sidecar differs from its durable bytes.");
        }
        var manifest = DurableProcessingProductManifestJson.Parse(sidecar);
        var payload = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        if (payload.LongLength != manifest.ByteLength ||
            !string.Equals(ProcessingIdentity.ComputePayloadSha256(payload), manifest.Artifact.ChecksumSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Committed metadata payload conflicts with its manifest.");
        }
        try
        {
            using var _ = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Committed metadata payload is not valid JSON.", exception);
        }
        var recipe = ProcessingIdentity.CreateRecipeIdentity(manifest.Artifact.Recipe);
        var product = new ProcessingProduct(
            manifest.Artifact.Role,
            manifest.Artifact.Variant,
            manifest.OutputIdentitySha256,
            manifest.Artifact.MediaType,
            null,
            payload,
            manifest.Artifact.ChecksumSha256,
            recipe,
            manifest.Algorithms,
            manifest.Artifact.SourceArtifactIds,
            TimeSpan.FromTicks(manifest.TotalIntegrationTicks),
            manifest.Compatibility);
        return new RestoredProcessingOutput(
            manifest.Artifact.ArtifactId,
            manifest.Artifact.CreatedUtc,
            null,
            product);
    }

    private static async Task WriteAtomicallyAsync(
        string storageRoot,
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = string.Concat(destinationPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // FlushAsync does not provide a flush-to-disk contract.
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, Path.GetDirectoryName(destinationPath)!);
            try
            {
                File.Move(temporaryPath, destinationPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                var existing = await File.ReadAllBytesAsync(destinationPath, cancellationToken).ConfigureAwait(false);
                if (!existing.AsSpan().SequenceEqual(content.Span))
                {
                    throw new InvalidDataException("Existing metadata evidence conflicts with immutable content.");
                }
                return;
            }
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, destinationPath);
            RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(destinationPath)!);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record RestoredProcessingOutput(
        Guid ArtifactId,
        DateTimeOffset CreatedUtc,
        FrameArtifact? Artifact,
        ProcessingProduct Product);

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
           current.Location == candidate.Location &&
           current.Controls.EffectiveExposure == candidate.Controls.EffectiveExposure &&
           current.Controls.EffectiveGain == candidate.Controls.EffectiveGain &&
           current.Controls.EffectiveOffset == candidate.Controls.EffectiveOffset &&
           current.Controls.TemperatureSetpointC == candidate.Controls.TemperatureSetpointC;
}
