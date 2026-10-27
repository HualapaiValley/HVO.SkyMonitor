using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Transforms committed prediction metadata into the existing selectable typed layer, without propagation.</summary>
internal sealed class CaptureSatelliteLayerProcessingStep(CaptureProcessingStepMetadata metadata, CaptureSatelliteLayerOptions options,
    IOrbitalSnapshotReader snapshots)
    : ConfigurableCaptureProcessingStep<CaptureSatelliteLayerOptions>(metadata, options), ICaptureProcessingGraphStep,
      IMultiOutputCaptureProcessingGraphStep, IRequiredCaptureProcessingDependencies
{
    internal const string Alias = "CaptureSatelliteLayer";
    public bool Enabled => true;
    public string RecipeName => PresentationProcessingProducts.LayerRecipeName;
    public string? SharedStepVersion => OrbitalTrackPresentation.ProducerVersion;
    public ProcessingOperationKind? SharedOperationKind => ProcessingOperationKind.Transform;
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => Options.InputVariant + "-layer";
    public string? OutputSchemaVersion => PresentationLayerPayloadV1.CurrentSchemaVersion;
    public string? OutputMediaType => PresentationLayerPayloadJson.MediaType;
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata };
    public IReadOnlyList<CaptureProcessingOutputDescriptor> Outputs =>
    [
        new(OutputRole, OutputVariant, RecipeName, OutputSchemaVersion,
            new(RecipeName, "1.0.0", OrbitalTrackPresentation.ProducerVersion, ProcessingOperationKind.Transform), MediaType: OutputMediaType),
        new(OutputRole, Options.InputVariant + "-source", "capture-satellite-source", CaptureSatelliteProducts.SourceSchema,
            new("capture-satellite-source", "1.0.0", CaptureSatelliteProducts.Version, ProcessingOperationKind.Transform),
            MediaType: CaptureSatelliteProducts.SourceMediaType)
    ];
    public IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements =>
    [
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { "capture-satellite-evidence" },
            new HashSet<string> { CaptureSatelliteProducts.InputSchema }, Options.InputVariant + "-inputs"),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { "capture-satellite-status" },
            new HashSet<string> { CaptureSatelliteProducts.StatusSchema }, Options.InputVariant + "-status"),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { CaptureOrbitalPredictionRecipe.Name },
            new HashSet<string> { CaptureOrbitalPredictionJson.SchemaVersion }, Options.InputVariant, Required: false)
    ];

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        var products = context.GetDependencyProducts();
        var input = CaptureSatelliteProducts.ParseInput(products.Single(p => p.Variant == Options.InputVariant + "-inputs").Payload.Span);
        var retainedStatus = products.Single(p => p.Variant == Options.InputVariant + "-status");
        var result = products.SingleOrDefault(p => p.Variant == Options.InputVariant)?.Payload;
        var descriptor = context.ReconstructionDescriptor ?? throw new InvalidDataException("Satellite layers require their raw descriptor.");
        var raw = context.Artifacts?.Raw ?? throw new InvalidDataException("Satellite layers require their exact raw capture.");
        var source = CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, raw, descriptor.Artifact.Variant,
            context.AcquisitionTiming, descriptor) with
        {
            MediaType = descriptor.Artifact.MediaType,
            CreatedUtc = descriptor.Artifact.CreatedUtc,
            ObservationStartedUtc = descriptor.Timing.ExposureStartedUtc,
            ObservationEndedUtc = descriptor.Timing.ExposureEndedUtc
        };
        OrbitalSnapshotReadResult? exact = null;
        if (input.Plan is not null && CaptureSatelliteProducts.ReadSnapshotManifest(input) is { } manifest)
            exact = await snapshots.ReadExactAsync(manifest.ManifestSha256, cancellationToken).ConfigureAwait(false);
        var rebuilt = CaptureSatelliteProducts.Create(input, source, Options.InputVariant, result, exact);
        if (rebuilt.Single(p => p.SchemaVersion == CaptureSatelliteProducts.StatusSchema).ChecksumSha256 != retainedStatus.ChecksumSha256)
            throw new InvalidDataException("Retained satellite presentation status differs from its canonical result.");
        LayeredPresentationCaptureProcessing.Add(context, rebuilt.Where(static p =>
            p.SchemaVersion == CaptureSatelliteProducts.SourceSchema || p.MediaType == PresentationLayerPayloadJson.MediaType).ToArray());
    }
}

public sealed class CaptureSatelliteLayerOptions
{
    [Required, MinLength(1), MaxLength(96)]
    public string InputVariant { get; init; } = "capture-satellites-v1";
}
