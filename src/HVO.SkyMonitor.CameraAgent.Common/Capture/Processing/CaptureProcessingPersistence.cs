using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Imaging;
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
    ICaptureProcessingFaultInjector? faultInjector = null) : IProcessingRetentionHolds, IProcessingOutputExpiration
{
    private readonly string _storageRoot = Path.GetFullPath(options.Value.RawIngressRoot);
    private readonly SqliteCaptureProcessingStore _store = store;
    private readonly IFrameStorageService _frameStorage = frameStorage;
    private readonly CaptureProcessingTelemetry _telemetry = telemetry;
    private readonly ILogger<CaptureProcessingPersistence> _logger = logger;
    private readonly ICaptureProcessingFaultInjector _faultInjector =
        faultInjector ?? NullCaptureProcessingFaultInjector.Instance;
    private readonly DerivedProductLifecycleOptions _lifecycleOptions = options.Value.DerivedProductLifecycle;
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    internal ValueTask InitializeAsync(CancellationToken cancellationToken)
        => _store.InitializeAsync(cancellationToken);

    internal ValueTask<DurableProcessingNode?> ReadNodeAsync(
        Guid captureId,
        string nodeId,
        CancellationToken cancellationToken)
        => _store.ReadNodeAsync(captureId, nodeId, cancellationToken);

    internal ValueTask<UnavailableNodeResolution> ResolveUnavailableNodeAsync(
        Guid captureId, string nodeId, string planSha256, CancellationToken cancellationToken)
        => _store.ResolveUnavailableNodeAsync(captureId, nodeId, planSha256, cancellationToken);

    internal ValueTask DeleteOutputlessNodeAsync(
        Guid captureId,
        string nodeId,
        long workId,
        string? leaseToken,
        CancellationToken cancellationToken)
        => _store.DeleteOutputlessNodeAsync(captureId, nodeId, workId, leaseToken, cancellationToken);

    public ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsAsync(
        string storageRoot,
        CancellationToken cancellationToken)
        => PathsEqual(storageRoot, _storageRoot)
            ? _store.ReadRetentionHoldsAsync(cancellationToken)
            : ValueTask.FromResult<IReadOnlyList<ProcessingRetentionHold>>([]);

    public async ValueTask<int> ExpireOutputsAsync(
        string storageRoot,
        DateTimeOffset committedBeforeUtc,
        IReadOnlySet<string> heldAbsolutePaths,
        CancellationToken cancellationToken)
    {
        if (!PathsEqual(storageRoot, _storageRoot)) return 0;
        var reconciler = new DerivedProductReconciler(_storageRoot, _store, _lifecycleOptions, _timeProvider);
        var deletedFiles = 0;
        foreach (var unavailable in new[] { false, true })
        {
            long? cursorTimestamp = null;
            string? cursorOutput = null;
            do
            {
                var page = unavailable
                    ? await _store.ReadUnavailableExpirationPageAsync(
                        _timeProvider.GetUtcNow().AddDays(-_lifecycleOptions.DiagnosticRetentionDays),
                        cursorTimestamp, cursorOutput, _lifecycleOptions.ReconciliationBatchSize, cancellationToken).ConfigureAwait(false)
                    : await _store.ReadAvailableExpirationPageAsync(
                        committedBeforeUtc, cursorTimestamp, cursorOutput,
                        _lifecycleOptions.ReconciliationBatchSize, cancellationToken).ConfigureAwait(false);
                foreach (var candidate in page.Items)
                {
                    var sourcePaths = candidate.AvailabilityState == "Quarantined" && candidate.QuarantineRelativePath is { } quarantine
                        ? Directory.Exists(ResolveSafePath(quarantine))
                            ? Directory.EnumerateFiles(ResolveSafePath(quarantine)).Order(StringComparer.Ordinal).ToArray()
                            : []
                        : new[] { ResolveSafePath(candidate.PayloadRelativePath), ResolveSafePath(candidate.SidecarRelativePath) }
                            .Where(File.Exists).ToArray();
                    if (sourcePaths.Any(heldAbsolutePaths.Contains) && candidate.AvailabilityState == "Available") continue;
                    var operation = new ProcessingLifecycleOperation(
                        $"delete:{Guid.NewGuid():N}", "delete", candidate.OutputIdentitySha256,
                        sourcePaths.FirstOrDefault() is { } first ? Relative(first) : null,
                        sourcePaths.Skip(1).FirstOrDefault() is { } second ? Relative(second) : null,
                        $"processing-deletion-tombstones/{Guid.NewGuid():N}", "retention-expired",
                        sourcePaths.Where(File.Exists).Sum(static path => new FileInfo(path).Length),
                        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                    await _store.PlanLifecycleOperationAsync(operation, cancellationToken).ConfigureAwait(false);
                    await reconciler.ResumeOperationAsync(operation, cancellationToken).ConfigureAwait(false);
                    deletedFiles += sourcePaths.Length;
                }
                cursorTimestamp = page.NextTimestamp;
                cursorOutput = page.NextOutputIdentitySha256;
            }
            while (cursorTimestamp is not null);
        }
        long? diagnosticTimestamp = null;
        long? diagnosticId = null;
        do
        {
            var diagnostics = await _store.ReadDiagnosticExpirationPageAsync(
                _timeProvider.GetUtcNow().AddDays(-_lifecycleOptions.DiagnosticRetentionDays),
                diagnosticTimestamp, diagnosticId, _lifecycleOptions.ReconciliationBatchSize,
                cancellationToken).ConfigureAwait(false);
            foreach (var diagnostic in diagnostics.Items)
            {
                if (diagnostic.QuarantineRelativePath is { } quarantine)
                {
                    var path = ResolveSafePath(quarantine);
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(path)!);
                }
                await _store.DeleteDiagnosticAsync(diagnostic.DiagnosticId, cancellationToken).ConfigureAwait(false);
            }
            diagnosticTimestamp = diagnostics.NextRecordedUnixMilliseconds;
            diagnosticId = diagnostics.NextDiagnosticId;
        }
        while (diagnosticTimestamp is not null);
        return deletedFiles;
    }

    private string Relative(string path) => Path.GetRelativePath(_storageRoot, path).Replace(Path.DirectorySeparatorChar, '/');

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
        foreach (var product in products)
        {
            ValidateProductForPublication(product);
        }
        var lifecycleGate = StorageLifecycleLock.ForRoot(_storageRoot);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outputs = new List<DurableProcessingOutput>(products.Count);
            foreach (var product in products)
            {
                var stopwatch = Stopwatch.StartNew();
                using var activity = CaptureProcessingTelemetry.ActivitySource.StartActivity("processing-artifact.persist");
                if (product.Layout is null && IsDurableLayoutlessProduct(product))
                {
                    outputs.Add(await PersistMetadataProductAsync(
                        rawCapture.Manifest.Descriptor, node, product, cancellationToken).ConfigureAwait(false));
                }
                else if (product.Layout is null)
                {
                    throw new InvalidDataException(
                        "Only JSON metadata and JPEG image products may use layoutless CameraAgent durable persistence.");
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
                        _storageRoot, artifact, descriptor, node.Id, cancellationToken).ConfigureAwait(false);
                    var relativePayloadPath = NormalizeRelativePath(stored.RelativePath);
                    var evidenceJson = CaptureContractJson.Serialize(new ArtifactManifestV2(
                        ArtifactManifestV2.CurrentSchemaVersion,
                        descriptor,
                        relativePayloadPath,
                        artifact.Frame.Metadata.Scene,
                        node.Id));
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
                        artifact.RecipeVersion,
                        null,
                        null,
                        null));
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
        if (output.ProductManifest is { } committedManifest)
        {
            ValidateLayoutlessOutputFacts(output, committedManifest);
        }
        var payloadPath = ResolveSafePath(output.PayloadRelativePath);
        var sidecarPath = ResolveSafePath(output.SidecarRelativePath);
        var sidecar = await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
        if (!sidecar.AsSpan().SequenceEqual(output.EvidenceJson))
        {
            throw new InvalidDataException("Committed processing output sidecar differs from its durable bytes.");
        }
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
        frame = frame with { Metadata = frame.Metadata with { Scene = manifest.Scene } };
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
        if (!IsDurableLayoutlessProduct(product) ||
            !string.Equals(checksum, product.ChecksumSha256, StringComparison.Ordinal) ||
            !string.Equals(expectedOutputIdentity, product.OutputIdentitySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Layoutless processing product has invalid immutable facts.");
        }
        var artifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256);
        DecodedImage? encodedImage = null;
        if (product.Role == FrameArtifactRole.Metadata)
        {
            try
            {
                using var _ = JsonDocument.Parse(product.Payload);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Layoutless processing product payload is not valid JSON.", exception);
            }
        }
        else
        {
            encodedImage = DecodeJpeg(product.Payload, "JPEG processing product payload is invalid.");
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
        var payloadPath = Path.Combine(directory, string.Concat(
            stem,
            product.Role == FrameArtifactRole.Metadata ? ".json" : ".jpg"));
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
        var isTypedMetadata = product.Kind == ProcessingProductKind.Metadata &&
            (product.SchemaVersion is not null || product.ContentIdentitySha256 is not null);
        IDurableProcessingProductManifest manifest = encodedImage is null && isTypedMetadata
            ? new DurableTypedMetadataProductManifestV3(
                DurableTypedMetadataProductManifestV3.CurrentSchemaVersion,
                sourceDescriptor.Capture,
                artifact,
                product.OutputIdentitySha256,
                product.Algorithms,
                product.Compatibility,
                product.TotalIntegration.Ticks,
                product.Payload.Length,
                payloadRelativePath,
                nullDocument.RootElement.Clone(),
                product.Kind,
                product.SchemaVersion!,
                product.ContentIdentitySha256!)
            : encodedImage is null
                ? new DurableProcessingProductManifestV1(
                    DurableProcessingProductManifestV1.CurrentSchemaVersion,
                    sourceDescriptor.Capture,
                    artifact,
                    product.OutputIdentitySha256,
                    product.Algorithms,
                    product.Compatibility,
                    product.TotalIntegration.Ticks,
                    product.Payload.Length,
                    payloadRelativePath,
                    nullDocument.RootElement.Clone())
                : new DurableEncodedProductManifestV2(
                DurableEncodedProductManifestV2.CurrentSchemaVersion,
                sourceDescriptor.Capture,
                artifact,
                product.OutputIdentitySha256,
                product.Algorithms,
                product.Compatibility,
                product.TotalIntegration.Ticks,
                product.Payload.Length,
                payloadRelativePath,
                nullDocument.RootElement.Clone(),
                encodedImage.Width,
                encodedImage.Height,
                encodedImage.PixelFormat,
                sourceId);
        var evidenceJson = DurableProcessingProductManifestJson.Serialize(manifest);
        var typedManifest = manifest as DurableTypedMetadataProductManifestV3;

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
            null,
            typedManifest?.Kind,
            typedManifest?.ProductSchemaVersion,
            typedManifest?.ContentIdentitySha256);
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
        var manifest = DurableProcessingProductManifestJson.Parse(sidecar);
        ValidateLayoutlessOutputFacts(output, manifest);
        var recipe = ProcessingIdentity.CreateRecipeIdentity(manifest.Artifact.Recipe);
        var payload = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        if (payload.LongLength != manifest.ByteLength ||
            !string.Equals(ProcessingIdentity.ComputePayloadSha256(payload), manifest.Artifact.ChecksumSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Committed metadata payload conflicts with its manifest.");
        }
        if (manifest is DurableProcessingProductManifestV1 or DurableTypedMetadataProductManifestV3)
        {
            try
            {
                using var _ = JsonDocument.Parse(payload);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Committed metadata payload is not valid JSON.", exception);
            }
        }
        else if (manifest is DurableEncodedProductManifestV2 encoded)
        {
            var decoded = DecodeJpeg(payload, "Committed JPEG payload is invalid.");
            if (decoded.Width != encoded.EncodedWidth || decoded.Height != encoded.EncodedHeight ||
                decoded.PixelFormat != encoded.EncodedPixelFormat)
            {
                throw new InvalidDataException("Committed JPEG dimensions or pixel format conflict with its manifest.");
            }
        }
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
            manifest.Compatibility)
        {
            Kind = manifest.Kind,
            SchemaVersion = manifest.ProductSchemaVersion,
            ContentIdentitySha256 = manifest.ContentIdentitySha256
        };
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

    private static bool IsDurableLayoutlessProduct(ProcessingProduct product)
        => product.Role == FrameArtifactRole.Metadata && IsJsonMediaType(product.MediaType) ||
           product.Role is FrameArtifactRole.Preview or FrameArtifactRole.AnnotatedPreview &&
           string.Equals(product.MediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase);

    private static void ValidateProductForPublication(ProcessingProduct product)
    {
        if (product.SourceArtifactIds is null || product.SourceArtifactIds.Count == 0 ||
            product.SourceArtifactIds.Count > LayeredPresentationJson.MaximumSourceArtifactCount ||
            product.SourceArtifactIds.Any(static source => source == Guid.Empty) ||
            product.SourceArtifactIds.Distinct().Count() != product.SourceArtifactIds.Count)
        {
            throw new InvalidDataException("Processing product source lineage is invalid or exceeds its durable bound.");
        }
    }

    private static bool IsJsonMediaType(string mediaType)
        => string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
           mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase) &&
           mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

    private static DecodedImage DecodeJpeg(ReadOnlyMemory<byte> payload, string message)
    {
        try
        {
            return JpegImageCodec.DecodeJpeg(payload);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            throw new InvalidDataException(message, exception);
        }
    }

    private static void ValidateLayoutlessOutputFacts(
        DurableProcessingOutput output,
        IDurableProcessingProductManifest manifest)
    {
        var expectedSidecarPath = NormalizeRelativePath(Path.ChangeExtension(
            manifest.RelativeArtifactPath,
            ".manifest.json"));
        var recipe = ProcessingIdentity.CreateRecipeIdentity(manifest.Artifact.Recipe);
        if (!string.Equals(NormalizeRelativePath(output.PayloadRelativePath),
                NormalizeRelativePath(manifest.RelativeArtifactPath), StringComparison.Ordinal) ||
            !string.Equals(NormalizeRelativePath(output.SidecarRelativePath), expectedSidecarPath, StringComparison.Ordinal) ||
            output.ArtifactId != manifest.Artifact.ArtifactId ||
            !string.Equals(output.OutputIdentitySha256, manifest.OutputIdentitySha256, StringComparison.Ordinal) ||
            !string.Equals(output.RecipeIdentitySha256, recipe.IdentitySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Committed layoutless processing output conflicts with its SQLite identity or paths.");
        }
    }
}
