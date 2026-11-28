using HVO.SkyMonitor.LocalFiles;
using HVO.SkyMonitor.CameraAgent.Common.Diagnostics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed record OnDemandMaterializationResult(
    Guid ArtifactId,
    string OutputIdentitySha256,
    string ChecksumSha256,
    long ByteLength,
    bool Replayed);

internal sealed partial class CaptureProcessingPersistence(
    IOptions<CameraAgentHostOptions> options,
    SqliteCaptureProcessingStore store,
    CaptureProcessingTelemetry telemetry,
    ILogger<CaptureProcessingPersistence> logger,
    ICaptureProcessingFaultInjector? faultInjector = null,
    SiloArtifactStorage? silo = null) :
    IProcessingRetentionHolds
{
    private readonly string _storageRoot = Path.GetFullPath(options.Value.RawIngressRoot);
    internal string StorageRoot => _storageRoot;
    internal Action<string>? BeforeMetadataDirectorySyncForTest { get; set; }

    internal async ValueTask<DurableProcessingOutput> RequireCommittedSceneAsync(
        Guid captureId, ProcessingProduct product, CancellationToken cancellationToken)
    {
        var output = await RequireOutputAsync(
            ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), captureId, cancellationToken).ConfigureAwait(false);
        if (output.OutputIdentitySha256 != product.OutputIdentitySha256 ||
            output.Artifact.ChecksumSha256 != product.ChecksumSha256 ||
            output.ContentIdentitySha256 != product.ContentIdentitySha256)
            throw new InvalidDataException("The canonical scene differs from the committed processing product.");
        return output;
    }
    internal ValueTask<string?> ReadAbandonedStandardWorkReasonAsync(Guid captureId, CancellationToken cancellationToken)
        => _store.ReadAbandonedStandardWorkReasonAsync(captureId, cancellationToken);

    // Centered catalog prediction shares the scene recipe and schema. Its fixed producer
    // and variant distinguish it from the renderer's scene, whose alias and variant are configurable.
    internal static bool IsExposurePredictionOutput(ReconstructionDescriptor? raw, string? producer, string? variant)
        => raw?.CenteredAcquisition is not null &&
            string.Equals(producer, ExposureCatalogPredictionProducts.StepAlias, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(variant, ExposureCatalogPredictionProducts.Variant, StringComparison.Ordinal);

    private readonly SqliteCaptureProcessingStore _store = store;
    private readonly CaptureProcessingTelemetry _telemetry = telemetry;
    private readonly ILogger<CaptureProcessingPersistence> _logger = logger;
    private readonly ICaptureProcessingFaultInjector _faultInjector =
        faultInjector ?? NullCaptureProcessingFaultInjector.Instance;
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    internal SiloArtifactStorage? Silo => silo;

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

    internal async ValueTask<OnDemandMaterializationResult> MaterializePresentationAsync(
        Guid captureId,
        Guid manifestArtifactId,
        IReadOnlyList<string> enabledLayerIdentitySha256,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(captureId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(manifestArtifactId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(enabledLayerIdentitySha256);
        if (enabledLayerIdentitySha256.Count > LayeredPresentationJson.MaximumLayerCount ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 128)
        {
            throw new ArgumentException("The on-demand materialization request is invalid.");
        }

        var lifecycleGate = StorageLifecycleLock.ForRoot(_storageRoot);
        CameraAgentWorkloadTelemetry.RecordMaterializationPending(1);
        try
        {
            await StorageLifecycleLock.WaitAsync(lifecycleGate, "capture-storage", _logger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CameraAgentWorkloadTelemetry.RecordMaterializationPending(-1);
        }
        try
        {
            var manifestOutput = await RequireOutputAsync(manifestArtifactId, captureId, cancellationToken).ConfigureAwait(false);
            var restoredManifest = await RestoreOutputAsync(manifestOutput, cancellationToken).ConfigureAwait(false);
            var manifest = LayeredPresentationJson.ParseManifest(restoredManifest.Product.Payload).Document
                ?? throw new InvalidDataException("The retained overlay manifest is invalid.");
            if (!string.Equals(manifest.ManifestIdentitySha256, restoredManifest.Product.ContentIdentitySha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The retained overlay manifest identity is invalid.");
            }

            var baseOutput = await RequireOutputAsync(manifest.BaseProduct.ArtifactId, captureId, cancellationToken).ConfigureAwait(false);
            var restoredBase = await RestoreOutputAsync(baseOutput, cancellationToken).ConfigureAwait(false);
            if (baseOutput.Descriptor is null || restoredBase.Artifact is null)
            {
                throw new InvalidDataException("The retained presentation base is not reconstructable.");
            }
            var baseArtifact = CreateProcessingArtifact(baseOutput, restoredBase);
            var manifestArtifact = CreateProcessingArtifact(manifestOutput, restoredManifest);
            var layerProducts = new List<PresentationLayerProductInput>(manifest.Layers.Count);
            foreach (var layer in manifest.Layers)
            {
                var output = await RequireOutputAsync(layer.SourceProduct.ArtifactId, captureId, cancellationToken).ConfigureAwait(false);
                var restored = await RestoreOutputAsync(output, cancellationToken).ConfigureAwait(false);
                layerProducts.Add(new(layer, CreateProcessingArtifact(output, restored)));
            }

            var started = _timeProvider.GetUtcNow();
            var product = CameraAgentWorkloadTelemetry.MaterializePacked(
                baseArtifact,
                manifestArtifact,
                manifest,
                layerProducts,
                enabledLayerIdentitySha256,
                "operator-stack-v1",
                cancellationToken: cancellationToken);
            var artifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256);
            if (await _store.ReadOutputByArtifactIdAsync(artifactId, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                if (existing.Capture.CaptureId != captureId || existing.AvailabilityState != "Available" ||
                    !string.Equals(existing.OutputIdentitySha256, product.OutputIdentitySha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The materialized artifact identity conflicts with durable state.");
                }
                var restored = await RestoreOutputAsync(existing, cancellationToken).ConfigureAwait(false);
                if (restored.ArtifactId != artifactId ||
                    !string.Equals(restored.Product.ChecksumSha256, product.ChecksumSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The materialized artifact facts conflict with durable evidence.");
                }
                _logger.PresentationMaterializationCompleted(
                    actor, product.OutputIdentitySha256, captureId, artifactId, replayed: true);
                return new(artifactId, product.OutputIdentitySha256, restored.Product.ChecksumSha256,
                    restored.Product.Payload.Length, true);
            }

            var frame = restoredBase.Artifact.Frame with
            {
                PixelData = product.Payload,
                PixelFormat = product.Layout!.PixelFormat,
                StrideBytes = product.Layout.StrideBytes,
                Metadata = restoredBase.Artifact.Frame.Metadata with { SourceId = "gallery-materialization" },
                Layout = product.Layout
            };
            var artifact = new FrameArtifact(
                artifactId, product.Role, frame, product.SourceArtifactIds,
                product.Recipe.Descriptor.ImplementationVersion);
            var descriptor = DerivativeDescriptorFactory.Create(
                baseOutput.Descriptor, artifactId, "gallery-materialization", product);
            var relativePayloadPath = GetRasterPublicationReference(artifact, descriptor);
            var evidenceJson = CaptureContractJson.Serialize(new ArtifactManifestV2(
                ArtifactManifestV2.CurrentSchemaVersion,
                descriptor,
                relativePayloadPath,
                frame.Metadata.Scene,
                "gallery-materialization"));
            var durableOutput = new DurableProcessingOutput(
                product.OutputIdentitySha256,
                artifactId,
                relativePayloadPath,
                NormalizeRelativePath(Path.ChangeExtension(relativePayloadPath, ".json")),
                evidenceJson,
                descriptor,
                null,
                product.Recipe.IdentitySha256,
                product.Algorithms,
                product.Compatibility,
                product.TotalIntegration,
                baseOutput.CaptureSequence,
                FrameArtifactRecipeVersion: artifact.RecipeVersion);
            var nodeId = $"gallery-materialization-{product.OutputIdentitySha256[..16]}";
            var planSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(new
            {
                Version = "gallery-materialization-node-v1",
                product.OutputIdentitySha256
            }));
            var node = new CaptureProcessingGraphNode(
                nodeId,
                OnDemandMaterializationStep.Instance,
                [],
                false,
                PresentationProcessingProducts.MaterializationRecipeName,
                FrameArtifactRole.AnnotatedPreview,
                product.Variant,
                planSha256);
            var selected = enabledLayerIdentitySha256.ToHashSet(StringComparer.Ordinal);
            var inputs = new List<DurableProcessingNodeInput>
            {
                Input(0, "base", baseArtifact, true),
                Input(1, "manifest", manifestArtifact, true)
            };
            inputs.AddRange(layerProducts.Select((layer, index) => Input(
                index + 2,
                layer.Layer.LayerKind,
                layer.Product,
                selected.Contains(layer.Layer.LayerIdentitySha256))));
            inputs.Add(new DurableProcessingNodeInput(
                inputs.Count, "CanonicalContext", "operator", null, null, null, null,
                "cameraagent-operator-v1", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actor))), false));
            var completed = _timeProvider.GetUtcNow();
            await PersistSiloOutputsAsync(baseOutput.Descriptor, [durableOutput], [product], nodeId, null, cancellationToken)
                .ConfigureAwait(false);
            await _store.WriteNodeAsync(
                captureId,
                node,
                DurableProcessingNodeStatus.Completed,
                null,
                1,
                null,
                started,
                completed,
                completed - started,
                ProcessingOutcomeStatus.Produced,
                inputs,
                0,
                null,
                [durableOutput],
                null,
                // On-demand materialization runs outside any graph execution, so there is no attempt row
                // to carry a route and no adapter dispatch to record.
                ProcessingNodeExecutionRoute.Unknown,
                cancellationToken).ConfigureAwait(false);
            _logger.PresentationMaterializationCompleted(
                actor, product.OutputIdentitySha256, captureId, artifactId, replayed: false);
            return new(artifactId, product.OutputIdentitySha256, product.ChecksumSha256, product.Payload.Length, false);
        }
        finally
        {
            lifecycleGate.Release();
        }

        static DurableProcessingNodeInput Input(
            int ordinal,
            string name,
            ProcessingArtifact artifact,
            bool selected) => new(
                ordinal, "Artifact", name, artifact.ArtifactId, artifact.Role, artifact.Variant,
                artifact.RecipeIdentitySha256, artifact.SchemaVersion,
                artifact.ContentIdentitySha256 ?? artifact.DescriptorIdentitySha256, selected);
    }

    internal ValueTask DeleteOutputlessNodeAsync(
        Guid captureId,
        string nodeId,
        long workId,
        string? leaseToken,
        CancellationToken cancellationToken)
        => _store.DeleteOutputlessNodeAsync(captureId, nodeId, workId, leaseToken, cancellationToken);

    internal ValueTask<DurableProcessingNode?> ReadExecutionNodeAsync(
        ProcessingExecutionContext execution,
        Guid captureId,
        string nodeId,
        CancellationToken cancellationToken)
        => _store.ReadExecutionNodeAsync(execution.ExecutionId, captureId, nodeId, cancellationToken);

    internal ValueTask BeginExecutionNodeAttemptAsync(
        ProcessingExecutionContext execution,
        CaptureProcessingGraphNode node,
        int attempt,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken)
        => _store.BeginNodeAttemptAsync(execution, node, attempt, startedUtc, cancellationToken);

    internal ValueTask CompleteOutputlessExecutionNodeAsync(
        ProcessingExecutionContext execution,
        CaptureProcessingGraphNode node,
        DurableProcessingNodeStatus status,
        string? reason,
        int attempt,
        DateTimeOffset completedUtc,
        TimeSpan? duration,
        ProcessingOutcomeStatus? outcome,
        ProcessingNodeExecutionRoute executionRoute,
        CancellationToken cancellationToken,
        ArtifactManifestV2? rawManifest = null,
        CaptureProcessingGraph? graph = null)
        => _store.CompleteOutputlessExecutionNodeAsync(
            execution, node, status, reason, attempt, completedUtc, duration, outcome,
            executionRoute, cancellationToken,
            rawManifest is not null && execution is { ExecutionClass: ProcessingGraphExecutionClass.Live, AllowAutomaticPublication: true }
                ? CenteredExposureProductReader.CreateOutputlessViewSettlements(rawManifest, node, status, reason, graph) : null);

    public ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsAsync(
        string storageRoot,
        CancellationToken cancellationToken)
        => PathsEqual(storageRoot, _storageRoot)
            ? _store.ReadRetentionHoldsAsync(cancellationToken)
            : ValueTask.FromResult<IReadOnlyList<ProcessingRetentionHold>>([]);

    /// <summary>
    /// The library masters named by the normalization plans of pending and running executions, and of ended executions'
    /// nodes whose outputs are still kept (#1475).
    /// </summary>
    internal ValueTask<IReadOnlyList<Guid>> GetNormalizationPlanMasterArtifactIdsAsync(
        string storageRoot,
        CancellationToken cancellationToken)
        => PathsEqual(storageRoot, _storageRoot)
            ? _store.ReadNormalizationPlanMasterArtifactIdsAsync(cancellationToken)
            : ValueTask.FromResult<IReadOnlyList<Guid>>([]);

    internal ValueTask<IReadOnlyList<SiloRetentionHold>> ReadSavedSiloViewHoldsAsync(CancellationToken cancellationToken) =>
        _store.ReadSavedSiloViewHoldsAsync(cancellationToken);

    internal ValueTask<IReadOnlyList<SiloSceneLineage>> ReadSiloSceneLineageAsync(CancellationToken cancellationToken) =>
        _store.ReadSiloSceneLineageAsync(cancellationToken);

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
        return await RestoreWindowInputsAsync(outputs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The shared upper bound on a durable window's inputs, applied to live and replay alike.</summary>
    internal int MaximumWindowInputs => _store.MaximumWindowInputs;

    /// <summary>Resolves and pins the derived input window a live execution node consumes.</summary>
    internal async ValueTask<IReadOnlyList<ProcessingArtifact>> ResolveLiveExecutionInputsAsync(
        ProcessingExecutionContext execution,
        string nodeId,
        ReconstructionDescriptor currentDescriptor,
        CancellationToken cancellationToken)
    {
        var outputs = await _store.ResolveLiveExecutionOutputWindowAsync(
            execution, nodeId, currentDescriptor, cancellationToken).ConfigureAwait(false);
        return await RestoreWindowInputsAsync(outputs, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<IReadOnlyList<ProcessingArtifact>> ReadFrozenExecutionInputsAsync(
        Guid executionId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        var outputs = await _store.ReadFrozenExecutionOutputsAsync(executionId, nodeId, cancellationToken)
            .ConfigureAwait(false);
        return await RestoreWindowInputsAsync(outputs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Restores committed outputs as processing inputs, verifying each sidecar, payload, recipe, and lineage identity
    /// against durable state. A window recipe and the nightly product generator both consume this.
    /// </summary>
    internal async ValueTask<IReadOnlyList<ProcessingArtifact>> RestoreWindowInputsAsync(
        IReadOnlyList<DurableProcessingOutput> outputs,
        CancellationToken cancellationToken)
    {
        var artifacts = new List<ProcessingArtifact>(outputs.Count);
        foreach (var output in outputs)
        {
            var restored = await RestoreOutputAsync(output, cancellationToken).ConfigureAwait(false);
            artifacts.Add(CreateProcessingArtifact(output, restored));
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
            byte[]? payload;
            if (silo is null)
            {
                var path = ResolveSafePath(entry.PayloadRelativePath);
                payload = File.Exists(path)
                    ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)
                    : null;
            }
            else
            {
                payload = await silo.ReadLiveRawAsync(entry.Descriptor, cancellationToken).ConfigureAwait(false);
            }
            if (payload is null) break;
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

    // Reads every raw input frozen for the execution, so a historical input that is missing, no longer committed or
    // pinned, or damaged fails the check rather than shortening the window.
    internal async ValueTask EnsureFrozenExecutionRawInputsIntactAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        var nodes = await _store.ReadFrozenExecutionRawInputNodesAsync(executionId, cancellationToken).ConfigureAwait(false);
        foreach (var (nodeId, inputCount) in nodes)
        {
            var inputs = await ReadFrozenExecutionRawInputsAsync(executionId, nodeId, cancellationToken).ConfigureAwait(false);
            if (inputs.Count != inputCount)
                throw new InvalidDataException("A frozen replay input is no longer retained.");
        }
    }

    internal async ValueTask<IReadOnlyList<ProcessingArtifact>> ReadFrozenExecutionRawInputsAsync(
        Guid executionId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        var entries = await _store.ReadFrozenExecutionRawInputsAsync(executionId, nodeId, cancellationToken).ConfigureAwait(false);
        var artifacts = new List<ProcessingArtifact>(entries.Count);
        var storage = silo ?? throw new InvalidOperationException("Frozen replay requires Silo storage.");
        foreach (var entry in entries)
        {
            var payload = await storage.ReadRawAsync(entry.Descriptor, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("A frozen replay input has no settled Silo publication.");
            if (!string.Equals(
                ProcessingIdentity.ComputePayloadSha256(payload),
                entry.PayloadSha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A frozen replay input checksum is invalid.");
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
                    entry.Descriptor.Controls.EffectiveExposure),
                Conditions: new ProcessingCaptureConditions(
                    entry.Descriptor.Controls.EffectiveGain,
                    entry.Descriptor.Controls.EffectiveOffset,
                    entry.Descriptor.Controls.EffectiveTemperatureC))
            {
                // The capture evidence a frozen normalization plan binds each light to.
                CaptureId = entry.Descriptor.Capture.CaptureId,
                DescriptorIdentitySha256 = entry.DescriptorSha256
            });
        }
        return artifacts;
    }

    internal ValueTask WriteNodeAsync(
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
        => WriteNodeAsync(
            rawCapture, node, status, reason, attempt, startedUtc, completedUtc, duration,
            outcome, workId, leaseToken, products, context, null, cancellationToken);

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
        ProcessingExecutionContext? execution,
        CancellationToken cancellationToken,
        CaptureProcessingGraph? graph = null)
    {
        foreach (var product in products)
        {
            ValidateProductForPublication(product);
        }
        var lifecycleGate = StorageLifecycleLock.ForRoot(_storageRoot);
        await StorageLifecycleLock.WaitAsync(lifecycleGate, "capture-storage", _logger, cancellationToken).ConfigureAwait(false);
        try
        {
            if (execution is not null)
            {
                await _store.EnsureExecutionLeaseAsync(execution, cancellationToken).ConfigureAwait(false);
            }
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
                    var source = artifact.Frame.Metadata.SourceId ?? node.Id;
                    var descriptor = DerivativeDescriptorFactory.Create(rawCapture.Manifest.Descriptor, artifact.ArtifactId, source, product);
                    var relativePayloadPath = GetRasterPublicationReference(artifact, descriptor);
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
                        NormalizeRelativePath(Path.ChangeExtension(relativePayloadPath, ".json")),
                        evidenceJson,
                        descriptor,
                        null,
                        product.Recipe.IdentitySha256,
                        product.Algorithms,
                        product.Compatibility,
                        product.TotalIntegration,
                        rawCapture.Manifest.Descriptor.Capture.CaptureSequence,
                        null,
                        null,
                        null,
                        FrameArtifactRecipeVersion: artifact.RecipeVersion));
                }
                stopwatch.Stop();
                _telemetry.RecordPersistence(node, product, stopwatch.Elapsed);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            if (execution?.ExecutionClass == ProcessingGraphExecutionClass.Replay && silo is not null)
            {
                await PublishReplayOutputsAsync(rawCapture.Manifest.Descriptor, node.Id, execution.ExecutionId,
                    outputs, products, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await PersistSiloOutputsAsync(rawCapture.Manifest.Descriptor, outputs, products, node.Id, execution?.ExecutionId,
                    cancellationToken).ConfigureAwait(false);
            }
            _faultInjector.Inject(CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit, node.Id);
            var inputEvidence = context.GetCurrentInputEvidence();
            var centeredProducts = rawCapture.Manifest.CenteredExposure is not null &&
                execution is { ExecutionClass: ProcessingGraphExecutionClass.Live, AllowAutomaticPublication: true }
                ? CenteredExposureProductReader.CreateSettlements(rawCapture.Manifest, node, status, reason, outputs, inputEvidence, graph)
                : null;
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
                inputEvidence,
                workId,
                leaseToken,
                outputs,
                execution,
                context.CurrentExecutionRoute,
                cancellationToken,
                centeredProducts).ConfigureAwait(false);
            if (outputs.Count > 0 && _logger.IsEnabled(LogLevel.Debug))
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

    private static string GetRasterPublicationReference(FrameArtifact artifact, ReconstructionDescriptor descriptor)
    {
        ValidateVersionedDescriptorAgreement(descriptor, artifact, artifact.Frame);
        return $"silo/products/{artifact.ArtifactId:N}.bin";
    }

    private static void ValidateVersionedDescriptorAgreement(
        ReconstructionDescriptor descriptor,
        FrameArtifact artifact,
        CameraFrame frame)
    {
        var validation = descriptor.Validate();
        var sources = artifact.SourceArtifactIds ?? Array.Empty<Guid>();
        double? frameTemperature = double.IsFinite(frame.Metadata.TemperatureC) ? frame.Metadata.TemperatureC : null;
        if (!validation.IsValid ||
            descriptor.Artifact.ArtifactId != artifact.ArtifactId ||
            descriptor.Artifact.Role != artifact.Role ||
            !descriptor.Artifact.SourceArtifactIds.SequenceEqual(sources) ||
            descriptor.Layout.Width != frame.Width ||
            descriptor.Layout.Height != frame.Height ||
            descriptor.Layout.PixelFormat != frame.PixelFormat ||
            descriptor.Layout.StrideBytes != (frame.StrideBytes ?? checked(frame.Width * HVO.SkyMonitor.Imaging.ImageLayout.BytesPerPixel(frame.PixelFormat))) ||
            descriptor.Layout.ByteLength != frame.PixelData.Length ||
            descriptor.Timing.ExposureStartedUtc != frame.TimestampUtc.ToUniversalTime() ||
            descriptor.Controls.EffectiveExposure != frame.Metadata.Exposure ||
            descriptor.Controls.EffectiveGain != frame.Metadata.Gain ||
            descriptor.Controls.EffectiveOffset != frame.Metadata.Offset ||
            descriptor.Controls.EffectiveTemperatureC != frameTemperature ||
            !string.Equals(descriptor.Artifact.SourceId, frame.Metadata.SourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Reconstruction descriptor does not match the stored artifact ({validation.ReasonCode ?? "descriptor.mismatch"}).",
                nameof(descriptor));
        }
    }

    private async ValueTask PublishReplayOutputsAsync(ReconstructionDescriptor raw, string nodeId, Guid executionId,
        List<DurableProcessingOutput> outputs, IReadOnlyList<ProcessingProduct> products,
        CancellationToken cancellationToken)
    {
        var storage = silo ?? throw new InvalidOperationException("Replay output publication requires Silo storage.");
        var newOutputs = new List<DurableProcessingOutput>(outputs.Count);
        var newProducts = new List<ProcessingProduct>(outputs.Count);
        for (var index = 0; index < outputs.Count; index++)
        {
            var output = outputs[index];
            var existing = await _store.ReadOutputByArtifactIdAsync(output.ArtifactId, cancellationToken).ConfigureAwait(false);
            if (existing is null || existing.EvidenceJson.AsSpan().SequenceEqual(output.EvidenceJson))
            {
                newOutputs.Add(output);
                newProducts.Add(products[index]);
                continue;
            }

            // A replay may associate the same immutable raster with a different node alias.
            // Its existing Silo member and evidence keep their original producer and version.
            if (existing.Capture.CaptureId != raw.Capture.CaptureId ||
                existing.OutputIdentitySha256 != output.OutputIdentitySha256 ||
                !SqliteCaptureProcessingStore.OutputEvidenceMatches(existing.EvidenceJson, output.EvidenceJson, allowProducerAlias: true) ||
                !await storage.VerifyMemberAsync(existing.ArtifactId, existing.OutputIdentitySha256,
                    products[index].Payload.Length, output.Artifact.ChecksumSha256, cancellationToken).ConfigureAwait(false) ||
                !await storage.VerifyMemberAsync(existing.ArtifactId, SiloArtifactStorage.ProductEvidenceRevision,
                    existing.EvidenceJson.LongLength, SiloHash.Sha256(existing.EvidenceJson), cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("A replay output identity conflicts with retained Silo evidence.");
        }
        await PersistSiloOutputsAsync(raw, newOutputs, newProducts, nodeId, executionId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PersistSiloOutputsAsync(ReconstructionDescriptor raw, List<DurableProcessingOutput> outputs,
        IReadOnlyList<ProcessingProduct> products, string nodeId, Guid? executionId, CancellationToken cancellationToken)
    {
        if (silo is null || outputs.Count == 0) return;
        var identity = SiloOutputGroupId(raw, nodeId, executionId, outputs.Select(static output => output.ArtifactId));
        var inputs = outputs.Select((output, index) => new SiloArtifactInput(output.ArtifactId, output.Artifact.Role.ToString(),
            output.Artifact.MediaType, output.Artifact.ChecksumSha256, products[index].Payload, output.EvidenceJson,
            output.ContentIdentitySha256, output.OutputIdentitySha256, nodeId, output.RecipeIdentitySha256)).ToArray();
        await silo.PublishProcessingAsync(identity, raw, inputs, cancellationToken).ConfigureAwait(false);
    }

    private static string SiloOutputGroupId(ReconstructionDescriptor raw, string nodeId, Guid? executionId,
        IEnumerable<Guid> artifacts) =>
        SiloCenteredViews.LowerHex(CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(new
        {
            raw.Capture.CaptureId,
            NodeId = nodeId,
            ExecutionId = executionId,
            Artifacts = artifacts.Order().ToArray()
        })));

    internal async Task<ProcessingArtifact> RestoreCenteredLinearAsync(
        ExposureProductRef expected, CancellationToken cancellationToken)
    {
        var saved = await _store.ReadOutputByArtifactIdAsync(expected.ArtifactId, cancellationToken).ConfigureAwait(false);
        if (saved is null || saved.AvailabilityState != "Available" ||
            saved.Capture.CaptureId != expected.Exposure.CaptureId ||
            saved.OutputIdentitySha256 != expected.OutputIdentitySha256 ||
            saved.Artifact.ChecksumSha256 != expected.PayloadSha256)
            throw new InvalidDataException("The retained linear artifact differs from its centered product binding.");
        var restored = await RestoreOutputAsync(saved, cancellationToken).ConfigureAwait(false);
        return CreateProcessingArtifact(saved, restored) with
        {
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(saved.Descriptor ??
                throw new InvalidDataException("A centered linear product requires a frame descriptor."))
        };
    }

    private async ValueTask<RestoredProcessingOutput> RestoreOutputAsync(
        DurableProcessingOutput output,
        CancellationToken cancellationToken)
    {
        if (output.ProductManifest is { } committedManifest)
        {
            ValidateLayoutlessOutputFacts(output, committedManifest);
        }
        var sidecar = silo is null
            ? await ReadCommittedFileAsync(ResolveSafePath(output.SidecarRelativePath), output.EvidenceJson.LongLength, cancellationToken)
                .ConfigureAwait(false)
            : await silo.ReadMemberAsync(output.ArtifactId, SiloArtifactStorage.ProductEvidenceRevision, output.EvidenceJson.LongLength,
                SiloHash.Sha256(output.EvidenceJson), cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The processing evidence has no settled Silo publication.");
        if (!sidecar.AsSpan().SequenceEqual(output.EvidenceJson))
        {
            throw new InvalidDataException("Committed processing output sidecar differs from its durable bytes.");
        }
        if (output.ProductManifest is not null)
        {
            return await RestoreMetadataProductAsync(
                output, sidecar, cancellationToken).ConfigureAwait(false);
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
        var payload = await ReadProcessingPayloadAsync(output, descriptor.Layout.ByteLength, cancellationToken).ConfigureAwait(false);
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
            output.FrameArtifactRecipeVersion);
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
            BeforeMetadataDirectorySyncForTest, provisionStorageRoot: false, cancellationToken, writeLocalCopies: silo is null).ConfigureAwait(false);

    internal static async ValueTask<DurableProcessingOutput> CopyMetadataProductAsync(
        string storageRoot,
        ReconstructionDescriptor sourceDescriptor,
        string sourceId,
        ProcessingProduct product,
        CancellationToken cancellationToken)
    {
        storageRoot = Path.GetFullPath(storageRoot);
        return await PersistMetadataProductAsync(
            storageRoot, sourceDescriptor, sourceId, product, null, provisionStorageRoot: true, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<DurableProcessingOutput> PersistMetadataProductAsync(
        string storageRoot,
        ReconstructionDescriptor sourceDescriptor,
        string sourceId,
        ProcessingProduct product,
        Action<string>? beforeDirectorySync,
        bool provisionStorageRoot,
        CancellationToken cancellationToken,
        bool writeLocalCopies = true)
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
        if (product.MediaType == ExposureMeasurementProducts.MediaType)
        {
            _ = ExposureMeasurementProducts.VerifyRawBinding(ExposureMeasurementProducts.Read(product), sourceDescriptor);
        }
        else if (product.Role == FrameArtifactRole.Metadata)
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
        var payloadRelativePath = writeLocalCopies ? NormalizeRelativePath(Path.GetRelativePath(storageRoot, payloadPath)) :
            $"silo/products/{artifactId:N}{Path.GetExtension(payloadPath)}";
        var sidecarRelativePath = writeLocalCopies ? NormalizeRelativePath(Path.GetRelativePath(storageRoot, sidecarPath)) :
            $"silo/products/{artifactId:N}.manifest.json";
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

        if (writeLocalCopies)
        {
            if (provisionStorageRoot) CameraAgentFileSystem.EnsureStorageRootDurable(storageRoot);
            Directory.CreateDirectory(directory);
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, directory);
            // Visibility can be left behind by a failed earlier namespace barrier. Reestablish
            // the entire owned directory chain before acknowledging an idempotent retry.
            beforeDirectorySync?.Invoke(directory);
            RawIngressFileStore.SyncDirectoryHierarchy(storageRoot, directory);
            var existingEvidence = await ValidateExistingMetadataEvidenceAsync(
                payloadPath, sidecarPath, product.Payload, product.ChecksumSha256, evidenceJson, cancellationToken).ConfigureAwait(false);
            if (existingEvidence is not null)
            {
                evidenceJson = existingEvidence;
                manifest = DurableProcessingProductManifestJson.Parse(existingEvidence);
            }
            if (!File.Exists(payloadPath))
            {
                await WriteAtomicallyAsync(storageRoot, payloadPath, product.Payload, beforeDirectorySync, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                RawIngressFileStore.SyncFile(storageRoot, payloadPath);
            }
            if (!File.Exists(sidecarPath))
            {
                await WriteAtomicallyAsync(storageRoot, sidecarPath, evidenceJson, beforeDirectorySync, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                RawIngressFileStore.SyncFile(storageRoot, sidecarPath);
            }
        }

        var typedManifest = manifest as DurableTypedMetadataProductManifestV3;
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
            typedManifest?.Kind,
            typedManifest?.ProductSchemaVersion,
            typedManifest?.ContentIdentitySha256);
    }

    private static async ValueTask<byte[]?> ValidateExistingMetadataEvidenceAsync(
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
            if (!sidecar.AsSpan().SequenceEqual(expectedSidecar.Span) &&
                !ProducerAliasEquivalent(sidecar, expectedSidecar))
            {
                throw new InvalidDataException("Existing metadata sidecar conflicts with the requested output identity.");
            }
            return sidecar;
        }
        return null;
    }

    private static bool ProducerAliasEquivalent(
        ReadOnlyMemory<byte> existing,
        ReadOnlyMemory<byte> requested)
    {
        var existingManifest = DurableProcessingProductManifestJson.Parse(existing);
        var requestedManifest = DurableProcessingProductManifestJson.Parse(requested);
        return NormalizeProducerAlias(existingManifest).AsSpan().SequenceEqual(
            NormalizeProducerAlias(requestedManifest));
    }

    private static byte[] NormalizeProducerAlias(IDurableProcessingProductManifest manifest)
        => DurableProcessingProductManifestJson.Serialize(manifest switch
        {
            DurableProcessingProductManifestV1 value => value with
            {
                Artifact = value.Artifact with { SourceId = "producer-alias" }
            },
            DurableEncodedProductManifestV2 value => value with
            {
                Artifact = value.Artifact with { SourceId = "producer-alias" },
                ProducerStepId = "producer-alias"
            },
            DurableTypedMetadataProductManifestV3 value => value with
            {
                Artifact = value.Artifact with { SourceId = "producer-alias" }
            },
            _ => throw new InvalidDataException("Durable processing product manifest type is unsupported.")
        });

    private async ValueTask<byte[]> ReadProcessingPayloadAsync(DurableProcessingOutput output, long length,
        CancellationToken cancellationToken) => silo is null
        ? await ReadCommittedFileAsync(ResolveSafePath(output.PayloadRelativePath), length, cancellationToken).ConfigureAwait(false)
        : await silo.ReadMemberAsync(output.ArtifactId, output.OutputIdentitySha256, length, output.Artifact.ChecksumSha256,
            cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException("The processing artifact has no settled Silo publication.");

    private async ValueTask<RestoredProcessingOutput> RestoreMetadataProductAsync(
        DurableProcessingOutput output,
        byte[] sidecar,
        CancellationToken cancellationToken)
    {
        var manifest = DurableProcessingProductManifestJson.Parse(sidecar);
        ValidateLayoutlessOutputFacts(output, manifest);
        var recipe = ProcessingIdentity.CreateRecipeIdentity(manifest.Artifact.Recipe);
        var payload = await ReadProcessingPayloadAsync(output, manifest.ByteLength, cancellationToken).ConfigureAwait(false);
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
        if (product.MediaType == ExposureMeasurementProducts.MediaType &&
            ExposureMeasurementProducts.Read(product).Input.Raw.CaptureId != output.Capture.CaptureId)
        {
            throw new InvalidDataException("The exposure measurement differs from the durable capture.");
        }
        return new RestoredProcessingOutput(
            manifest.Artifact.ArtifactId,
            manifest.Artifact.CreatedUtc,
            null,
            product);
    }

    /// <summary>Restores only the length committed in the journal, never a corrupt file's untrusted length.</summary>
    private static async Task<byte[]> ReadCommittedFileAsync(string path, long expectedLength, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedLength < 0 || expectedLength > Array.MaxLength)
            throw new InvalidDataException("Committed processing file length is unsupported.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
        if (stream.Length != expectedLength)
            throw new InvalidDataException("Committed processing file length differs from its durable length.");
        var bytes = new byte[(int)expectedLength];
        try
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Committed processing file was truncated during restoration.", exception);
        }
        if (stream.Length != expectedLength || await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Committed processing file grew during restoration.");
        return bytes;
    }

    internal static async Task WriteAtomicallyAsync(
        string storageRoot,
        string destinationPath,
        ReadOnlyMemory<byte> content,
        Action<string>? beforeDirectorySync,
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
                CameraAgentFileSystem.FlushFile(stream);
            }
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, Path.GetDirectoryName(destinationPath)!);
            try
            {
                CameraAgentFileSystem.PublishPrepared(storageRoot, temporaryPath, destinationPath, overwrite: false);
            }
            catch (IOException exception) when (exception is not FileSystemFaultException { Kind: FileSystemFaultKind.Containment } && File.Exists(destinationPath))
            {
                RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, destinationPath);
                var existing = await File.ReadAllBytesAsync(destinationPath, cancellationToken).ConfigureAwait(false);
                if (!existing.AsSpan().SequenceEqual(content.Span))
                {
                    throw new InvalidDataException("Existing metadata evidence conflicts with immutable content.");
                }
                RawIngressFileStore.SyncFile(storageRoot, destinationPath);
            }
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, destinationPath);
            beforeDirectorySync?.Invoke(Path.GetDirectoryName(destinationPath)!);
            RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(destinationPath)!);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                RetainedFileMutations.Delete(temporaryPath);
            }
        }
    }

    private sealed record RestoredProcessingOutput(
        Guid ArtifactId,
        DateTimeOffset CreatedUtc,
        FrameArtifact? Artifact,
        ProcessingProduct Product);

    private async ValueTask<DurableProcessingOutput> RequireOutputAsync(
        Guid artifactId,
        Guid captureId,
        CancellationToken cancellationToken)
    {
        var output = await _store.ReadOutputByArtifactIdAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (output is null || output.Capture.CaptureId != captureId || output.AvailabilityState != "Available")
        {
            throw new InvalidDataException("A retained presentation source is unavailable.");
        }
        return output;
    }

    private static ProcessingArtifact CreateProcessingArtifact(
        DurableProcessingOutput output,
        RestoredProcessingOutput restored)
    {
        var descriptor = output.Descriptor;
        return new ProcessingArtifact(
            output.ArtifactId,
            restored.Product.Role,
            restored.Product.Variant,
            restored.Product.Recipe.IdentitySha256,
            restored.Product.MediaType,
            restored.Product.Layout,
            restored.Product.Payload,
            restored.CreatedUtc,
            restored.Product.TotalIntegration,
            restored.Product.Compatibility,
            output.CaptureSequence,
            restored.Product.SourceArtifactIds,
            descriptor?.Timing.ExposureStartedUtc,
            descriptor is null
                ? null
                : ProcessingArtifact.ResolveObservationEndedUtc(
                    descriptor.Timing.ExposureStartedUtc,
                    descriptor.Timing.ExposureEndedUtc,
                    restored.Product.TotalIntegration),
            descriptor is null
                ? null
                : new ProcessingCaptureConditions(
                    descriptor.Controls.EffectiveGain,
                    descriptor.Controls.EffectiveOffset,
                    descriptor.Controls.EffectiveTemperatureC))
        {
            ProductKind = restored.Product.Kind,
            SchemaVersion = restored.Product.SchemaVersion,
            ContentIdentitySha256 = restored.Product.ContentIdentitySha256 ?? restored.Product.OutputIdentitySha256,
            CaptureId = output.Capture.CaptureId,
            DescriptorIdentitySha256 = null,
            RecipeDescriptor = restored.Product.Recipe.Descriptor
        };
    }

    private sealed class OnDemandMaterializationStep : ICaptureProcessingStep
    {
        internal static OnDemandMaterializationStep Instance { get; } = new();

        public string Name => "GalleryMaterialization";

        public int Order => int.MaxValue;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The on-demand materialization step is a durable operation descriptor only.");
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
