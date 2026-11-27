using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class SiloCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    SiloCaptureProcessingStepOptions options,
    ILatestFrameAccessor latestFrameAccessor,
    CaptureProcessingPersistence processingPersistence,
    ICaptureProcessingFaultInjector? faults = null)
    : ConfigurableCaptureProcessingStep<SiloCaptureProcessingStepOptions>(metadata, options),
        ICaptureProcessingArtifactConsumer, IDurableCaptureProcessingPostCommit
{
    internal const string StableAlias = "Storage";
    internal const string SiloArchiveEvidenceVersion = "cameraagent-silo-archive-v1";

    public IReadOnlySet<FrameArtifactRole> AcceptedDependencyRoles { get; } = new HashSet<FrameArtifactRole>
    {
        FrameArtifactRole.Raw,
        FrameArtifactRole.Calibrated,
        FrameArtifactRole.Combined,
        FrameArtifactRole.Preview,
        FrameArtifactRole.AnnotatedPreview,
        FrameArtifactRole.Metadata
    };

    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Artifacts is null)
        {
            context.AddProcessingOutcome(ProcessingOutcome.Skipped(ProcessingReasonCodes.MissingInput));
            return ValueTask.CompletedTask;
        }

        var explicitGraph = context.Config.Pipeline is { SchemaVersion: CapturePipelineSchemaVersions.ExplicitV2 };
        var artifacts = explicitGraph ? context.GetDependencyArtifacts() : context.AllArtifacts;
        var products = explicitGraph ? context.GetDependencyProducts() : context.ProcessingProducts;
        if (explicitGraph && artifacts.Count == 0 && products.Count == 0)
        {
            context.AddProcessingOutcome(ProcessingOutcome.Skipped(ProcessingReasonCodes.MissingInput));
            return ValueTask.CompletedTask;
        }

        context.RecordConsumedArtifacts(artifacts, products);
        if (Options.PublishArchive && context.RawCapture?.Manifest.CenteredExposure is not null &&
            context.ProcessingExecution is { ExecutionClass: ProcessingGraphExecutionClass.Live, AllowAutomaticPublication: true })
        {
            foreach (var artifact in artifacts)
            {
                if (context.GetProcessingProduct(artifact.ArtifactId) is { } product &&
                    context.GetDependencyPublicationPolicy(artifact.ArtifactId)?.Persistence != CaptureProcessingPersistenceMode.MemoryOnly)
                    context.RecordArchivePublication(artifact.ArtifactId.ToString("D"), product.OutputIdentitySha256);
            }
            if (artifacts.Any(artifact => context.GetProcessingProduct(artifact.ArtifactId) is not null))
                faults?.Inject(CaptureProcessingFaultPoint.AfterArchiveCopyStaged, context.CurrentNodeId!);
        }

        if (Options.UpdateLatestFrame && context.ProcessingExecution?.AllowAutomaticPublication != false)
        {
            foreach (var role in new[] { FrameArtifactRole.Raw, FrameArtifactRole.Combined })
            {
                if (artifacts.LastOrDefault(artifact => artifact.Role == role) is { } product)
                    latestFrameAccessor.Update(product);
            }
            var preview = artifacts.LastOrDefault(static artifact => artifact.Role == FrameArtifactRole.AnnotatedPreview)
                ?? artifacts.LastOrDefault(static artifact => artifact.Role == FrameArtifactRole.Preview);
            if (preview is not null)
                latestFrameAccessor.Update(preview);
        }
        return ValueTask.CompletedTask;
    }

    bool IDurableCaptureProcessingPostCommit.RequiresSuccessfulCommitCallback => Options.PublishArchive;

    async ValueTask IDurableCaptureProcessingPostCommit.OnCommittedAsync(
        CaptureDescriptorProcessingContext context, CancellationToken cancellationToken)
    {
        if (!Options.PublishArchive || context.CenteredExposure is null ||
            context.ProcessingExecution is not { ExecutionClass: ProcessingGraphExecutionClass.Live, AllowAutomaticPublication: true } execution)
            return;

        await processingPersistence.PublishSiloArchiveAsync(context.ReconstructionDescriptor!, execution.ExecutionId,
            context.CurrentNodeId ?? throw new InvalidOperationException("Centered Storage requires an execution node."),
            cancellationToken).ConfigureAwait(false);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SiloCaptureProcessingStepOptions
{
    public bool UpdateLatestFrame { get; init; } = true;

    public bool PublishArchive { get; init; }
}
