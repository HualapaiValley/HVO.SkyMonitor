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
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class NoOpFileStorageProcessingStep(
    CaptureProcessingStepMetadata metadata,
    NoOpFileStorageProcessingStepOptions options,
    ILatestFrameAccessor latestFrameAccessor,
    IFrameStorageService frameStorageService,
    IArtifactOutbox artifactOutbox,
    IOptions<CameraAgentHostOptions> hostOptions,
    ILogger<NoOpFileStorageProcessingStep> logger,
    CaptureProcessingPersistence? processingPersistence = null) : ConfigurableCaptureProcessingStep<NoOpFileStorageProcessingStepOptions>(metadata, options), ICaptureProcessingArtifactConsumer
{
    internal const string StableAlias = "Storage";

    private readonly ILatestFrameAccessor _latestFrameAccessor = latestFrameAccessor;
    private readonly IFrameStorageService _frameStorageService = frameStorageService;
    private readonly IArtifactOutbox _artifactOutbox = artifactOutbox;
    private readonly bool _centralIntegrationEnabled =
        hostOptions.Value.CentralIntegration.Mode == CentralIntegrationMode.Enabled;
    private readonly ILogger<NoOpFileStorageProcessingStep> _logger = logger;
    private readonly CaptureProcessingPersistence? _processingPersistence = processingPersistence;

    public IReadOnlySet<FrameArtifactRole> AcceptedDependencyRoles { get; } = new HashSet<FrameArtifactRole>
    {
        FrameArtifactRole.Calibrated,
        FrameArtifactRole.Combined,
        FrameArtifactRole.Preview,
        FrameArtifactRole.AnnotatedPreview,
        FrameArtifactRole.Metadata
    };

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Config.AgentId);
        var artifacts = context.Artifacts;
        if (artifacts is null)
        {
            _logger.NoOpStorageSkipped(Name);
            context.AddProcessingOutcome(ProcessingOutcome.Skipped(ProcessingReasonCodes.MissingInput));
            return;
        }

        _logger.NoOpStoragePlanned(Name, artifacts.Raw.Frame.TimestampUtc, Options.StorageRoot, Options.RetentionDays);
        var durableGraphOwnsStorage = !_centralIntegrationEnabled && context.RawCapture is { } durableRaw &&
            IsStoredUnderRoot(durableRaw.StoredFrame, Options.StorageRoot);
        var metadataAlreadyStoredUnderRoot = context.RawCapture is { } metadataRaw &&
            IsStoredUnderRoot(metadataRaw.StoredFrame, Options.StorageRoot);
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
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var artifact in selectedArtifacts)
            {
                var product = context.GetProcessingProduct(artifact.ArtifactId);
                var policy = ResolvePolicy(artifact, product);
                StoredFrameReference stored;
                ArtifactManifestV2? uploadManifest = null;
                if (artifact.Role == FrameArtifactRole.Raw && context.RawCapture is { } ingress)
                {
                    if (!IsStoredUnderRoot(ingress.StoredFrame, Options.StorageRoot))
                    {
                        continue;
                    }
                    stored = ingress.StoredFrame;
                    uploadManifest = ingress.Manifest;
                }
                else
                {
                    if (durableGraphOwnsStorage && product is not null)
                    {
                        continue;
                    }
                    if (product is not null && context.RawCapture is { } rawCapture)
                    {
                        var descriptor = DerivativeDescriptorFactory.Create(
                            rawCapture.Manifest.Descriptor,
                            artifact.ArtifactId,
                            artifact.Frame.Metadata.SourceId ?? Name,
                            product);
                        stored = await _frameStorageService.SaveAsync(
                            Options.StorageRoot,
                            artifact,
                            descriptor,
                            cancellationToken).ConfigureAwait(false);
                        uploadManifest = new ArtifactManifestV2(
                            ArtifactManifestV2.CurrentSchemaVersion,
                            descriptor,
                            stored.RelativePath,
                            artifact.Frame.Metadata.Scene);
                    }
                    else
                    {
                        stored = await _frameStorageService.SaveAsync(
                            Options.StorageRoot, artifact, cancellationToken).ConfigureAwait(false);
                    }
                }
                if (_centralIntegrationEnabled && (policy?.QueueForUpload ?? Options.QueueForUpload))
                {
                    if (uploadManifest is null)
                    {
                        throw new InvalidDataException("Only reconstructable manifest-v2 artifacts can be queued for upload.");
                    }
                    await _artifactOutbox.EnqueueAsync(
                        Options.StorageRoot, uploadManifest, cancellationToken).ConfigureAwait(false);
                }
            }
            foreach (var product in selectedProducts.Where(static product =>
                         product.Role == FrameArtifactRole.Metadata && product.Layout is null))
            {
                var policy = ResolvePolicy(product);
                if (_centralIntegrationEnabled && (policy?.QueueForUpload ?? Options.QueueForUpload))
                {
                    throw new InvalidOperationException("Layoutless metadata products cannot be queued for central frame upload.");
                }
                if (metadataAlreadyStoredUnderRoot)
                {
                    continue;
                }
                if (_processingPersistence is null || context.ReconstructionDescriptor is not { } descriptor)
                {
                    throw new InvalidOperationException("Layoutless metadata storage requires durable reconstruction context.");
                }
                await CaptureProcessingPersistence.CopyMetadataProductAsync(
                    Options.StorageRoot,
                    descriptor,
                    Name,
                    product,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (Options.UpdateLatestFrame)
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

    private ArtifactStoragePolicyOptions? ResolvePolicy(FrameArtifact artifact, HVO.SkyMonitor.Processing.ProcessingProduct? product)
        => (Options.Policies ?? [])
            .Where(policy => policy.Role is null || policy.Role == artifact.Role)
            .Where(policy => policy.Variant is null || string.Equals(policy.Variant, product?.Variant, StringComparison.Ordinal))
            .Where(policy => policy.RecipeName is null || string.Equals(
                policy.RecipeName, product?.Recipe.Descriptor.Name, StringComparison.Ordinal))
            .OrderByDescending(static policy =>
                (policy.Role is null ? 0 : 1) + (policy.Variant is null ? 0 : 1) + (policy.RecipeName is null ? 0 : 1))
            .FirstOrDefault();

    private ArtifactStoragePolicyOptions? ResolvePolicy(ProcessingProduct product)
        => (Options.Policies ?? [])
            .Where(policy => policy.Role is null || policy.Role == product.Role)
            .Where(policy => policy.Variant is null || string.Equals(policy.Variant, product.Variant, StringComparison.Ordinal))
            .Where(policy => policy.RecipeName is null || string.Equals(
                policy.RecipeName, product.Recipe.Descriptor.Name, StringComparison.Ordinal))
            .OrderByDescending(static policy =>
                (policy.Role is null ? 0 : 1) + (policy.Variant is null ? 0 : 1) + (policy.RecipeName is null ? 0 : 1))
            .FirstOrDefault();

    private static bool IsStoredUnderRoot(StoredFrameReference storedFrame, string storageRoot)
        => string.Equals(
            Path.GetFullPath(Path.Combine(storageRoot, storedFrame.RelativePath)),
            Path.GetFullPath(storedFrame.AbsolutePath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

}

public sealed class NoOpFileStorageProcessingStepOptions : IValidatableObject
{
    [Required(AllowEmptyStrings = false)]
    public string StorageRoot { get; init; } = "/tmp/camera";

    [Range(1, 3650)]
    public int RetentionDays { get; init; } = 30;

    public bool UpdateLatestFrame { get; init; } = true;

    public bool QueueForUpload { get; init; }

    public IReadOnlyList<ArtifactStoragePolicyOptions> Policies { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var selectors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in Policies ?? [])
        {
            if (policy.Role is null && policy.Variant is null && policy.RecipeName is null)
            {
                yield return new ValidationResult(
                    "Artifact storage policies require at least one role, variant, or recipe selector.",
                    [nameof(Policies)]);
            }
            if (policy.RetentionDays is { } retentionDays && retentionDays < RetentionDays)
            {
                yield return new ValidationResult(
                    "Artifact-specific retention may extend, but not shorten, the storage-root retention period.",
                    [nameof(Policies)]);
            }
            var key = $"{policy.Role}\0{policy.Variant}\0{policy.RecipeName}";
            if (!selectors.Add(key))
            {
                yield return new ValidationResult("Artifact storage policy selectors must be unique.", [nameof(Policies)]);
            }
        }
    }
}

public sealed class ArtifactStoragePolicyOptions
{
    public FrameArtifactRole? Role { get; init; }

    public string? Variant { get; init; }

    public string? RecipeName { get; init; }

    public bool? QueueForUpload { get; init; }

    [Range(1, 3650)]
    public int? RetentionDays { get; init; }
}
