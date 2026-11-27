using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class FileStorageCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    FileStorageCaptureProcessingStepOptions options,
    ILatestFrameAccessor latestFrameAccessor,
    IFrameStorageService frameStorageService,
    ILogger<FileStorageCaptureProcessingStep> logger,
    CaptureProcessingPersistence? processingPersistence = null,
    ICaptureProcessingFaultInjector? faults = null) : ConfigurableCaptureProcessingStep<FileStorageCaptureProcessingStepOptions>(metadata, options), ICaptureProcessingArtifactConsumer, IDurableCaptureProcessingPostCommit
{
    internal const string StableAlias = "Storage";
    internal const string SiloArchiveEvidenceVersion = "cameraagent-silo-archive-v1";

    private readonly ILatestFrameAccessor _latestFrameAccessor = latestFrameAccessor;
    private readonly IFrameStorageService _frameStorageService = frameStorageService;
    private readonly ILogger<FileStorageCaptureProcessingStep> _logger = logger;
    private readonly CaptureProcessingPersistence? _processingPersistence = processingPersistence;

    internal FileStorageCaptureProcessingStepOptions ConfiguredOptions => Options;

    public IReadOnlySet<FrameArtifactRole> AcceptedDependencyRoles { get; } = new HashSet<FrameArtifactRole>
    {
        FrameArtifactRole.Raw,
        FrameArtifactRole.Calibrated,
        FrameArtifactRole.Combined,
        FrameArtifactRole.Preview,
        FrameArtifactRole.AnnotatedPreview,
        FrameArtifactRole.Metadata
    };

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var allowAutomaticPublication = context.ProcessingExecution?.AllowAutomaticPublication != false;
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Config.AgentId);
        var artifacts = context.Artifacts;
        if (artifacts is null)
        {
            _logger.FileStorageSkipped(Name);
            context.AddProcessingOutcome(ProcessingOutcome.Skipped(ProcessingReasonCodes.MissingInput));
            return;
        }

        _logger.FileStorageStarted(Name, artifacts.Raw.Frame.TimestampUtc, Options.StorageRoot, Options.RetentionDays);
        var durableGraphOwnsStorage = context.ProcessingExecution is not null && context.RawCapture is { } durableRaw &&
            IsStoredUnderRoot(durableRaw.StoredFrame, Options.StorageRoot);
        var metadataAlreadyStoredUnderRoot = context.RawCapture is { } metadataRaw &&
            IsStoredUnderRoot(metadataRaw.StoredFrame, Options.StorageRoot);
        var exportsCenteredCopy = !metadataAlreadyStoredUnderRoot && context.RawCapture?.Manifest.CenteredExposure is not null &&
            context.ProcessingExecution is { ExecutionClass: ProcessingGraphExecutionClass.Live, AllowAutomaticPublication: true };
        var lifecycleGate = StorageLifecycleLock.ForRoot(Options.StorageRoot);
        var selectedArtifacts = IsExplicitPipeline(context.Config)
            ? context.GetDependencyArtifacts()
            : context.AllArtifacts;
        var selectedProducts = IsExplicitPipeline(context.Config)
            ? context.GetDependencyProducts()
            : context.ProcessingProducts;
        if (IsExplicitPipeline(context.Config) && selectedArtifacts.Count == 0 && selectedProducts.Count == 0)
        {
            context.AddProcessingOutcome(ProcessingOutcome.Skipped(ProcessingReasonCodes.MissingInput));
            return;
        }
        context.RecordConsumedArtifacts(selectedArtifacts, selectedProducts);
        if (_processingPersistence?.Silo is not null)
        {
            if (exportsCenteredCopy)
            {
                foreach (var artifact in selectedArtifacts)
                {
                    if (context.GetProcessingProduct(artifact.ArtifactId) is { } product &&
                        context.GetDependencyPublicationPolicy(artifact.ArtifactId)?.Persistence != CaptureProcessingPersistenceMode.MemoryOnly)
                        context.RecordArchivePublication(artifact.ArtifactId.ToString("D"), product.OutputIdentitySha256, SiloArchiveEvidenceVersion);
                }
                if (selectedArtifacts.Any(artifact => context.GetProcessingProduct(artifact.ArtifactId) is not null))
                    faults?.Inject(CaptureProcessingFaultPoint.AfterArchiveCopyStaged, context.CurrentNodeId!);
            }
            UpdateLatestFrame(selectedArtifacts, allowAutomaticPublication);
            return;
        }
        await StorageLifecycleLock.WaitAsync(lifecycleGate, "capture-storage", _logger, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var artifact in selectedArtifacts)
            {
                var product = context.GetProcessingProduct(artifact.ArtifactId);
                var producerStepId = context.GetDependencyProducerStepId(artifact.ArtifactId);
                var policy = ResolvePolicy(producerStepId, artifact, product);
                var publication = context.GetDependencyPublicationPolicy(artifact.ArtifactId);
                if (publication is not null || artifact.Role == FrameArtifactRole.Raw && context.RawCapture is not null ||
                    durableGraphOwnsStorage && product is not null)
                {
                    continue;
                }
                if (product is null || context.RawCapture is not { } rawCapture)
                {
                    throw new InvalidDataException(
                        "File storage requires a reconstruction descriptor for every artifact.");
                }
                // Canonical pixels already carry their producer identity.
                var manifestProducerStepId = metadataAlreadyStoredUnderRoot || policy?.StepId is not null
                    ? producerStepId : null;
                await SaveDerivativeAsync(context, rawCapture, artifact, product,
                    producerStepId, manifestProducerStepId, exportsCenteredCopy, cancellationToken).ConfigureAwait(false);
            }
            foreach (var product in selectedProducts.Where(static product => product.Layout is null))
            {
                var artifactId = CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256);
                var producerStepId = context.GetDependencyProducerStepId(artifactId);
                var publication = context.GetDependencyPublicationPolicy(artifactId);
                if (publication is not null || product.Role != FrameArtifactRole.Metadata ||
                    SceneProvenance.IsRetainedProjectedSceneSchemaVersion(product.SchemaVersion) ||
                    metadataAlreadyStoredUnderRoot)
                {
                    continue;
                }
                if (_processingPersistence is null || context.ReconstructionDescriptor is not { } descriptor)
                {
                    throw new InvalidOperationException("Layoutless metadata storage requires durable reconstruction context.");
                }
                var productRoot = Options.StorageRoot;
                await CaptureProcessingPersistence.CopyMetadataProductAsync(
                    productRoot,
                    descriptor,
                    producerStepId ?? Name,
                    product,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        UpdateLatestFrame(selectedArtifacts, allowAutomaticPublication);
    }

    private void UpdateLatestFrame(IReadOnlyList<FrameArtifact> selectedArtifacts, bool allowAutomaticPublication)
    {
        if (allowAutomaticPublication && Options.UpdateLatestFrame)
        {
            var raw = selectedArtifacts.LastOrDefault(static artifact => artifact.Role == FrameArtifactRole.Raw);
            if (raw is not null)
            {
                _latestFrameAccessor.Update(raw);
            }
            var combined = selectedArtifacts.LastOrDefault(static artifact => artifact.Role == FrameArtifactRole.Combined);
            if (combined is not null)
            {
                _latestFrameAccessor.Update(combined);
            }
            var display = selectedArtifacts.LastOrDefault(static artifact => artifact.Role == FrameArtifactRole.AnnotatedPreview) ??
                selectedArtifacts.LastOrDefault(static artifact => artifact.Role == FrameArtifactRole.Preview);
            if (display is not null)
            {
                _latestFrameAccessor.Update(display);
            }
        }

    }

    private static bool IsExplicitPipeline(CameraModuleConfig config)
        => config.Pipeline is { SchemaVersion: CapturePipelineSchemaVersions.ExplicitV2 };

    private async ValueTask<(StoredFrameReference Stored, ReconstructionDescriptor Descriptor)> SaveDerivativeAsync(
        CaptureProcessingContext context, RawIngress.RawCaptureReceipt raw, FrameArtifact artifact, ProcessingProduct product,
        string? producer, string? manifestProducer, bool exportsCenteredCopy, CancellationToken cancellationToken)
    {
        var descriptor = await DerivativeDescriptorFactory.CreateForStorageAsync(
            Options.StorageRoot, raw.Manifest.Descriptor, artifact, artifact.Frame.Metadata.SourceId ?? Name, product, cancellationToken)
            .ConfigureAwait(false);
        if (!exportsCenteredCopy)
        {
            var saved = manifestProducer is null
                ? await _frameStorageService.SaveAsync(Options.StorageRoot, artifact, descriptor, cancellationToken).ConfigureAwait(false)
                : await _frameStorageService.SaveAsync(Options.StorageRoot, artifact, descriptor, manifestProducer, cancellationToken)
                    .ConfigureAwait(false);
            return (saved, descriptor);
        }
        if (_frameStorageService is not FileSystemFrameStorageService frameStorage ||
            context.ProcessingExecution is not { } execution || context.CurrentNodeId is not { } storageNode ||
            producer is null)
            throw new InvalidDataException("The centered archive copy has no authorized Storage execution.");

        var (stored, savedDescriptor, identity) = await frameStorage.SaveArchiveAsync(Options.StorageRoot, artifact, descriptor, manifestProducer,
            raw, execution, producer, product.OutputIdentitySha256, product.ContentIdentitySha256, cancellationToken)
            .ConfigureAwait(false);
        context.RecordArchivePublication(stored.RelativePath.Replace('\\', '/'), identity);
        faults?.Inject(CaptureProcessingFaultPoint.AfterArchiveCopyStaged, storageNode);
        return (stored, savedDescriptor);
    }

    bool IDurableCaptureProcessingPostCommit.RequiresSuccessfulCommitCallback => true;

    async ValueTask IDurableCaptureProcessingPostCommit.OnCommittedAsync(
        CaptureDescriptorProcessingContext context, CancellationToken cancellationToken)
    {
        if (context.CenteredExposure is null ||
            context.ProcessingExecution is not { ExecutionClass: ProcessingGraphExecutionClass.Live, AllowAutomaticPublication: true } execution)
            return;
        if (_processingPersistence is null || context.CurrentNodeId is not { } storageNode)
            throw new InvalidOperationException("Centered Storage requires durable archive publication evidence.");

        if (_processingPersistence.Silo is not null)
        {
            await _processingPersistence.PublishSiloArchiveAsync(
                context.ReconstructionDescriptor!, execution.ExecutionId, storageNode, cancellationToken).ConfigureAwait(false);
            return;
        }

        var copies = await _processingPersistence.ReadCommittedArchivePublicationsAsync(
            execution.ExecutionId, storageNode, cancellationToken).ConfigureAwait(false);
        if (copies.Count == 0)
            return;
        var gate = StorageLifecycleLock.ForRoot(Options.StorageRoot);
        await StorageLifecycleLock.WaitAsync(gate, "archive-receipt", _logger, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var copy in copies)
            {
                faults?.Inject(CaptureProcessingFaultPoint.AfterArchiveCopyCommittedBeforeReceipt, storageNode);
                await FileSystemFrameStorageService.PublishCommittedArchiveAsync(
                    Options.StorageRoot, copy.RelativePath, copy.Identity, execution.ExecutionId, storageNode, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private ArtifactStoragePolicyOptions? ResolvePolicy(
        string? producerStepId, FrameArtifact artifact, ProcessingProduct? product)
        => ResolvePolicy(producerStepId, artifact.Role, product?.Variant, product?.Recipe.Descriptor.Name);

    private ArtifactStoragePolicyOptions? ResolvePolicy(string? producerStepId, ProcessingProduct product)
        => ResolvePolicy(producerStepId, product.Role, product.Variant, product.Recipe.Descriptor.Name);

    private ArtifactStoragePolicyOptions? ResolvePolicy(
        string? producerStepId, FrameArtifactRole role, string? variant, string? recipeName)
        => (Options.Policies ?? [])
            .Where(policy => policy.StepId is null || string.Equals(policy.StepId, producerStepId, StringComparison.OrdinalIgnoreCase))
            .Where(policy => policy.Role is null || policy.Role == role)
            .Where(policy => policy.Variant is null || string.Equals(policy.Variant, variant, StringComparison.Ordinal))
            .Where(policy => policy.RecipeName is null || string.Equals(policy.RecipeName, recipeName, StringComparison.Ordinal))
            .OrderByDescending(static policy =>
                (policy.StepId is null ? 0 : 1) + (policy.Role is null ? 0 : 1) +
                (policy.Variant is null ? 0 : 1) + (policy.RecipeName is null ? 0 : 1))
            .FirstOrDefault();

    private static bool IsStoredUnderRoot(StoredFrameReference storedFrame, string storageRoot)
        => string.Equals(
            Path.GetFullPath(Path.Combine(storageRoot, storedFrame.RelativePath)),
            Path.GetFullPath(storedFrame.AbsolutePath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

}

public sealed class FileStorageCaptureProcessingStepOptions : IValidatableObject
{
    [Required(AllowEmptyStrings = false)]
    public string StorageRoot { get; init; } = "/tmp/camera";

    [Range(1, 3650)]
    public int RetentionDays { get; init; } = 30;

    public bool UpdateLatestFrame { get; init; } = true;


    public IReadOnlyList<ArtifactStoragePolicyOptions> Policies { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var selectors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in Policies ?? [])
        {
            if (policy.StepId is not null && string.IsNullOrWhiteSpace(policy.StepId))
            {
                yield return new ValidationResult("Artifact storage policy step selectors cannot be empty.", [nameof(Policies)]);
            }
            if (policy.StepId is null && policy.Role is null && policy.Variant is null && policy.RecipeName is null)
            {
                yield return new ValidationResult(
                    "Artifact storage policies require at least one step, role, variant, or recipe selector.",
                    [nameof(Policies)]);
            }
            if (policy.RetentionDays is { } retentionDays && retentionDays < RetentionDays)
            {
                yield return new ValidationResult(
                    "Artifact-specific retention may extend, but not shorten, the storage-root retention period.",
                    [nameof(Policies)]);
            }
            var key = $"{policy.StepId?.ToUpperInvariant()}\0{policy.Role}\0{policy.Variant}\0{policy.RecipeName}";
            if (!selectors.Add(key))
            {
                yield return new ValidationResult("Artifact storage policy selectors must be unique.", [nameof(Policies)]);
            }
        }
    }
}

public sealed class ArtifactStoragePolicyOptions
{
    public string? StepId { get; init; }

    public FrameArtifactRole? Role { get; init; }

    public string? Variant { get; init; }

    public string? RecipeName { get; init; }


    [Range(1, 3650)]
    public int? RetentionDays { get; init; }
}
